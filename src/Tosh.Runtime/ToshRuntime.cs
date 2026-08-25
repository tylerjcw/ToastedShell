using System.Collections.Concurrent;
using System.Text;

namespace Tosh.Runtime;

public sealed class ToshRuntime :
    IToastHostSignals,
    IToastDiagnosticSink,
    IToastExecutionObserver,
    IToastSessionRedirection,
    IToastBackgroundJobHost,
    IToastRuntimeNamespaceFactory,
    IToastEnvironmentExporter,
    IToastAutoCdCommandFactory,
    IToastCommandHost
{
    private int _nextJobId;
    private long _nextHistoryId;
    private readonly ConcurrentDictionary<int, ShellJob> _jobs = new();
    private readonly object _historyGate = new();
    private readonly object _displaySelectionGate = new();
    private readonly object _directoryStackGate = new();
    private readonly List<string> _directoryStack = new();
    private int _directoryStackIndex = -1;
    private readonly Dictionary<object, DisplayColumnSelection> _displaySelections = new(ReferenceEqualityComparer.Instance);
    private bool _historyStorageInitialized;
    private bool _historyWriteThroughEnabled;
    private bool _directoryStackStorageInitialized;
    private string? _directoryStackFilePath;

    public ToshRuntime(TextWriter? output = null, TextWriter? error = null)
    {
        Output = output ?? TextWriter.Null;
        Error = error ?? TextWriter.Null;

        string initialDirectory;
        try
        {
            initialDirectory = Environment.CurrentDirectory;
        }
        catch (Exception)
        {
            initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        PushDirectory(initialDirectory);
        Commands = new ShellCommandRegistry();
        // One table, two views: the language resolves and registers through
        // ICommandTable, the shell keeps the registry's alias and lookup members.
        Language = new ToastRuntime
        {
            Commands = Commands,
            CurrentDirectory = initialDirectory,
            Output = ToastStreams.FromWriter(_output),
            Error = ToastStreams.FromWriter(_error),
            HostSignals = this,
            Diagnostics = this,
            ExecutionObserver = this,
            SessionRedirection = this,
            BackgroundJobs = this,
            RuntimeNamespaceFactory = this,
            EnvironmentExporter = this,
            AutoCdCommandFactory = this,
            CommandHost = this,
        };
        DisplayPreferences = new DisplayPreferences();
        DisplayProfiles = DisplayProfileRegistry.CreateDefault(DisplayPreferences);
        Formatter = new ObjectFormatter(DisplayProfiles);
        Display = new DisplayEngine(Formatter);
        Display.Preferences = DisplayPreferences;
        Inspector = new ObjectInspector(Formatter);
        Config = new ToshConfig(Display, DisplayPreferences, ToshConfigDefaults.GetDefaultConfigDirectory(), Options);
        Config.Shell.Usings.Bind((DotNetTypeResolver)TypeResolver);
        TerminalGlyphs.Initialize(Config.Tty);
        PathUtilities.DirectoryAliases = Config.Shell.Dirs;
        Display.TableTheme = Config.Theme.Tables;
        History = new List<CommandHistoryEntry>();
        ExportedEnvironmentVariables = new HashSet<string>(StringComparer.Ordinal);
        ExecHandler = new DefaultShellExecHandler();
        Terminal = new TerminalControl();
        SetLastExitCode(0);
        SetLastResult(null);
    }

    private TextWriter _output = TextWriter.Null;
    private TextWriter _error = TextWriter.Null;

    /// <summary>The session's stdout.</summary>
    /// <remarks>
    /// `TOAST-0015`. The writer stays the shell's, and the language sees it as one
    /// destination among files, pipes and buffers — <see cref="ToastRuntime.Output"/> is
    /// kept in step here rather than duplicated, so a host that assigns this still redirects
    /// correctly and the language never reaches for a <c>TextWriter</c>.
    /// </remarks>
    public TextWriter Output
    {
        get => _output;
        set
        {
            _output = value;

            // Null-guarded because the constructor assigns the writers before it builds
            // `Language`, and reordering that is worse than tolerating one check: the
            // registry the language runtime is composed from is built in between.
            if (Language is not null) { Language.Output = ToastStreams.FromWriter(value); }
        }
    }

    /// <summary>The session's stderr, mirrored to the language the same way.</summary>
    public TextWriter Error
    {
        get => _error;
        set
        {
            _error = value;

            if (Language is not null) { Language.Error = ToastStreams.FromWriter(value); }
        }
    }

    /// <summary>
    /// Mirrors a language-owned redirection into the shell session for the lifetime of a
    /// pipeline. The language streams themselves remain untouched; only the legacy shell
    /// writers used by shell commands and process plumbing are scoped here (`TOAST-0006`).
    /// </summary>
    public IDisposable Begin(IToastStream? output, IToastStream? error)
    {
        var originalOutput = _output;
        var originalError = _error;

        if (output is not null)
        {
            _output = new ToastStreamWriter(output);
        }

        if (error is not null)
        {
            _error = new ToastStreamWriter(error);
        }

        return new SessionRedirectionScope(
            this,
            originalOutput,
            originalError,
            restoreOutput: output is not null,
            restoreError: error is not null);
    }

    private sealed class SessionRedirectionScope(
        ToshRuntime runtime,
        TextWriter originalOutput,
        TextWriter originalError,
        bool restoreOutput,
        bool restoreError) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (restoreOutput)
            {
                runtime._output = originalOutput;
            }

            if (restoreError)
            {
                runtime._error = originalError;
            }
        }
    }

    private sealed class ToastStreamWriter(IToastStream stream) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) => stream.WriteText(value.ToString());

        public override void Write(string? value)
        {
            if (value is not null)
            {
                stream.WriteText(value);
            }
        }

        public override void WriteLine(string? value)
            => stream.WriteTextLine(value ?? string.Empty);

        public override void Flush() => stream.Flush();

        public override Task WriteAsync(string? value)
            => value is null
                ? Task.CompletedTask
                : stream.WriteTextAsync(value, CancellationToken.None).AsTask();

        public override Task WriteLineAsync(string? value)
            => stream.WriteTextLineAsync(value ?? string.Empty, CancellationToken.None).AsTask();

        public override Task WriteAsync(
            ReadOnlyMemory<char> buffer,
            CancellationToken cancellationToken = default)
            => stream.WriteTextAsync(buffer.ToString(), cancellationToken).AsTask();

        public override Task WriteLineAsync(
            ReadOnlyMemory<char> buffer,
            CancellationToken cancellationToken = default)
            => stream.WriteTextLineAsync(buffer.ToString(), cancellationToken).AsTask();

        public override Task FlushAsync()
            => stream.FlushAsync(CancellationToken.None).AsTask();
    }

    public string CurrentDirectory
    {
        get => Language.CurrentDirectory;
        set => Language.CurrentDirectory = value;
    }

    /// <summary>
    /// The language's own runtime. TōSh composes one rather than being one: the members
    /// below that read <c>Language.</c> are the shell's view of language state, kept so
    /// <c>$tosh.Vars</c> and friends still work (`TOAST-0006`).
    /// </summary>
    public ToastRuntime Language { get; }

    public ShellCommandRegistry Commands { get; }

    /// <summary>
    /// Creates commands that launch programs on disk, or <see langword="null"/> in a
    /// host that does not run external processes.
    /// </summary>
    /// <remarks>
    /// Set by whichever layer owns process launching — <c>Tosh.Stdlib</c> wires it from
    /// its module initializer alongside <see cref="DefaultCommandRegistrar"/>. Left null,
    /// resolving a name to a program on <c>PATH</c> reports that this host does not run
    /// external commands, which is the honest answer for an embedded Tōast (`TOAST-0004`).
    /// </remarks>
    public IExternalCommandFactory? ExternalCommands
    {
        get => Language.ExternalCommands;
        set => Language.ExternalCommands = value;
    }

    public IObjectAccessor ObjectAccessor => Language.ObjectAccessor;

    public ITypeResolver TypeResolver => Language.TypeResolver;

    public IObjectInvoker Invoker => Language.Invoker;

    public DisplayPreferences DisplayPreferences { get; }

    public DisplayProfileRegistry DisplayProfiles { get; }

    public ObjectFormatter Formatter { get; }

    public DisplayEngine Display { get; }

    public ObjectInspector Inspector { get; }

    public ToshConfig Config { get; }

    /// <summary>
    /// Settings the language owns, independent of any shell (`TOAST-0006`).
    /// </summary>
    /// <remarks>
    /// Created before <see cref="Config"/> and passed into it, because the config
    /// sections that expose these values delegate here rather than holding their own
    /// storage. That ordering is what keeps
    /// <c>$tosh.Config.Shell.MaxRecursionDepth = 5</c> working from script while the
    /// language reads nothing from a config file.
    /// </remarks>
    public ToastOptions Options => Language.Options;

    public IList<CommandHistoryEntry> History { get; }

    public IDictionary<string, object?> Variables => Language.Variables;

    /// <summary>
    /// The live runtime namespace object exposed as <c>$tosh</c> by the language engine.
    /// Stored as <see cref="object"/> to avoid a Core -> Language assembly dependency.
    /// </summary>
    public object? RuntimeNamespace { get; set; }

    /// <summary>
    /// Composes TōSh's runtime namespace around the evaluator-backed script and function
    /// views. The first root is published for completion and introspection; forked engines
    /// receive their own live root without replacing that session-level reference.
    /// </summary>
    public IShellRecordObject CreateRuntimeNamespace(
        IToastScriptNamespace script,
        IToastFunctionNamespace function)
    {
        var runtimeNamespace = new ToshRuntimeNamespace(this, script, function);
        RuntimeNamespace ??= runtimeNamespace;
        return runtimeNamespace;
    }

    public IDictionary<string, object?> Classes => Language.Classes;

    /// <summary>
    /// CLR types emitted for globally-declared <c>raw struct</c>s, keyed by
    /// declared name. The runtime-level counterpart of
    /// <c>LexicalScope.NativeTypes</c>; consulted by the type resolver so a
    /// global raw struct is nameable in native signatures.
    /// </summary>
    public IDictionary<string, Type> NativeTypes => Language.NativeTypes;

    public IDictionary<string, object?> Modules => Language.Modules;

    public ISet<string> LoadedModules => Language.LoadedModules;

    public ISet<string> ExportedEnvironmentVariables { get; }

    public IReadOnlyList<object?> InvocationArguments
    {
        get => Language.InvocationArguments;
        set => Language.InvocationArguments = value;
    }

    public IShellExecHandler ExecHandler { get; set; }

    public TerminalControl Terminal { get; }

    public IInlinePromptProvider? InlinePrompts { get; set; }

    public ICommandLineInsertionSink? CommandLineInsertion { get; set; }

    public ShellEventBus Events => Language.Events;

    public Func<ShellEventSender>? EventSenderFactory
    {
        get => Language.EventSenderFactory;
        set => Language.EventSenderFactory = value;
    }

    public IShellBlockExecutor? BlockExecutor
    {
        get => Language.BlockExecutor;
        set => Language.BlockExecutor = value;
    }

    public IShellEvaluator? Evaluator
    {
        get => Language.Evaluator;
        set => Language.Evaluator = value;
    }

    public bool HistoryStorageInitialized => _historyStorageInitialized;

    public bool HistoryWriteThroughEnabled => _historyWriteThroughEnabled;

    public bool ExitRequested { get; private set; }

    public bool IsLoginShell { get; set; }

    /// <summary>
    /// Set to true after the first exit attempt when background jobs are running.
    /// Reset after each command so the warning re-arms if new jobs start.
    /// </summary>
    public bool ExitWarningIssued { get; set; }

    public int LastExitCode { get; private set; }

    public object? LastResult { get; private set; }

    public TimeSpan? LastCommandDuration { get; private set; }

    /// <summary>
    /// Rendered text of the diagnostic from the most recent command that escaped to the REPL
    /// (or any caller that captures it via <see cref="SetLastDiagnostic"/>). Cleared when a
    /// command completes without an error.
    /// </summary>
    public string? LastDiagnostic { get; private set; }

    /// <summary>
    /// The raw exception associated with <see cref="LastDiagnostic"/>, if any. Cleared on success.
    /// </summary>
    public Exception? LastError { get; private set; }

    /// <summary>
    /// Wall-clock start time of the most recent recorded command, set by the REPL
    /// (or other callers) just before execution.
    /// </summary>
    public DateTimeOffset? LastStartedAt { get; private set; }

    public StartupProfileData? StartupProfile { get; set; }

    public long NextHistoryId => Math.Max(1, _nextHistoryId + 1);

    public IReadOnlyList<ShellJob> GetJobs()
    {
        ReapCompletedJobs();
        return _jobs.Values
            .OrderBy(job => job.Id)
            .ToArray();
    }

    /// <summary>
    /// Snapshots every currently-tracked job (including completed-but-unreaped ones) without
    /// triggering reaping. Used by <c>scope</c> to enumerate scope-owned jobs after the block
    /// runs without losing fast-completing children to the reaper.
    /// </summary>
    public IReadOnlyList<ShellJob> GetJobsSnapshot()
    {
        return _jobs.Values
            .OrderBy(job => job.Id)
            .ToArray();
    }

    public int AllocateJobId()
    {
        return Interlocked.Increment(ref _nextJobId);
    }

    public ShellJob RegisterJob(ShellJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        ReapCompletedJobs();
        _jobs[job.Id] = job;
        return job;
    }

    /// <summary>
    /// Materializes a language-neutral background request as a TōSh job, allocates its
    /// session identifier, and registers it for <c>jobs</c>/<c>wait-for</c>.
    /// </summary>
    public object StartExternalPipeline(ToastBackgroundPipelineRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stages = request.Stages
            .Select(stage => new ShellJobProcessSpec(stage.ResolvedPath, stage.Arguments))
            .ToArray();
        var redirections = request.Redirections
            .Select(redirection => new ShellJobRedirectionSpec(
                redirection.Path,
                redirection.Stream switch
                {
                    ToastBackgroundRedirectionStream.Output => ShellJobRedirectionStream.Output,
                    ToastBackgroundRedirectionStream.Error => ShellJobRedirectionStream.Error,
                    ToastBackgroundRedirectionStream.OutputThenError => ShellJobRedirectionStream.OutputThenError,
                    ToastBackgroundRedirectionStream.ErrorThenOutput => ShellJobRedirectionStream.ErrorThenOutput,
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(redirection.Stream),
                        redirection.Stream,
                        "Unknown background redirection stream."),
                },
                redirection.Mode switch
                {
                    ToastBackgroundRedirectionMode.Truncate => ShellJobRedirectionMode.Truncate,
                    ToastBackgroundRedirectionMode.Append => ShellJobRedirectionMode.Append,
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(redirection.Mode),
                        redirection.Mode,
                        "Unknown background redirection mode."),
                }))
            .ToArray();

        var job = RegisterJob(ShellJob.StartExternalPipeline(
            AllocateJobId(),
            request.CommandText,
            request.WorkingDirectory,
            stages,
            request.InitialInput,
            redirections));

        return job.ToInfo();
    }

    public bool TryGetJob(int id, out ShellJob job)
    {
        return _jobs.TryGetValue(id, out job!);
    }

    public void KillAllJobs()
    {
        foreach (var job in _jobs.Values)
        {
            job.Kill();
        }

        _jobs.Clear();
    }

    private void ReapCompletedJobs()
    {
        foreach (var kvp in _jobs)
        {
            if (kvp.Value.Status is not ShellJobStatus.Running and not ShellJobStatus.Suspended)
            {
                _jobs.TryRemove(kvp.Key, out _);
            }
        }
    }

    public CommandHistoryEntry? RecordHistory(string source)
    {
        lock (_historyGate)
        {
            if (!TryAddHistoryEntryUnsafe(source, DateTimeOffset.Now))
            {
                return null;
            }

            var entry = History[^1];

            if (_historyWriteThroughEnabled)
            {
                SaveHistoryUnsafe();
            }

            return entry;
        }
    }

    public void InitializeHistoryStorage(bool writeThrough)
    {
        lock (_historyGate)
        {
            _historyStorageInitialized = true;
            _historyWriteThroughEnabled = writeThrough;
            ReloadHistoryUnsafe();
        }
    }

    public void ReloadHistoryFromFile()
    {
        lock (_historyGate)
        {
            _historyStorageInitialized = true;
            ReloadHistoryUnsafe();
        }
    }

    public void SaveHistoryToFile()
    {
        lock (_historyGate)
        {
            _historyStorageInitialized = true;
            SaveHistoryUnsafe();
        }
    }

    public void ClearHistory()
    {
        lock (_historyGate)
        {
            _historyStorageInitialized = true;
            History.Clear();
            _nextHistoryId = 0;
            SaveHistoryUnsafe();
        }
    }

    public bool RemoveHistoryEntry(long id)
    {
        lock (_historyGate)
        {
            var removed = false;

            for (var index = History.Count - 1; index >= 0; index--)
            {
                if (History[index].Id != id)
                {
                    continue;
                }

                History.RemoveAt(index);
                removed = true;
                break;
            }

            if (!removed)
            {
                return false;
            }

            if (_historyStorageInitialized)
            {
                SaveHistoryUnsafe();
            }

            return true;
        }
    }

    public void InitializeDirectoryStackStorage()
    {
        lock (_directoryStackGate)
        {
            var startupDirectory = CurrentDirectory;
            _directoryStackFilePath = Path.Combine(ToshConfigDefaults.GetDefaultStateDirectory(), "dirstack.json");
            _directoryStackStorageInitialized = true;

            var state = DirectoryStackFileStore.Load(_directoryStackFilePath);

            if (state.Entries.Count > 0)
            {
                _directoryStack.Clear();
                _directoryStack.AddRange(state.Entries);
                _directoryStackIndex = state.Index;
            }

            if (_directoryStack.Count == 0)
            {
                _directoryStack.Add(startupDirectory);
                _directoryStackIndex = 0;
            }
            else
            {
                var currentIndex = Math.Clamp(_directoryStackIndex, 0, _directoryStack.Count - 1);
                var currentEntry = _directoryStack[currentIndex];
                var comparison = OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;

                if (!string.Equals(currentEntry, startupDirectory, comparison))
                {
                    if (currentIndex < _directoryStack.Count - 1)
                    {
                        _directoryStack.RemoveRange(currentIndex + 1, _directoryStack.Count - currentIndex - 1);
                    }

                    _directoryStack.Add(startupDirectory);
                    _directoryStackIndex = _directoryStack.Count - 1;
                }
                else
                {
                    _directoryStackIndex = currentIndex;
                }
            }

            CurrentDirectory = startupDirectory;
            SaveDirectoryStackUnsafe();
        }
    }

    public void PushDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        lock (_directoryStackGate)
        {
            if (_directoryStackIndex < _directoryStack.Count - 1)
            {
                _directoryStack.RemoveRange(_directoryStackIndex + 1, _directoryStack.Count - _directoryStackIndex - 1);
            }

            _directoryStack.Add(path);
            _directoryStackIndex = _directoryStack.Count - 1;
            SaveDirectoryStackUnsafe();
        }
    }

    /// <summary>Creates TōSh's directory-navigation command for AutoCd.</summary>
    public IShellCommand CreateAutoCdCommand(string resolvedPath)
        => new HostedAutoCdCommand(this, resolvedPath);

    private sealed class HostedAutoCdCommand(ToshRuntime runtime, string resolvedPath)
        : IShellCommand
    {
        public string Name => "cd";

        public string Description => "Auto-cd into a directory.";

        public string Usage => "cd [path]";

        public async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
        {
            var directoryInfo = new DirectoryInfo(resolvedPath);

            if (!directoryInfo.Exists)
            {
                throw new InvalidOperationException($"Directory '{resolvedPath}' does not exist.");
            }

            var oldDirectory = FileSystemEntry.From(new DirectoryInfo(runtime.CurrentDirectory));
            runtime.CurrentDirectory = directoryInfo.FullName;
            runtime.PushDirectory(directoryInfo.FullName);

            var newDirectory = FileSystemEntry.From(directoryInfo);
            var sender = runtime.EventSenderFactory?.Invoke()
                ?? new ShellEventSender(Function: null, Script: null, Line: null);
            var evt = new DirectoryChangedEvent(oldDirectory, newDirectory, sender);
            await runtime.Events.RaiseAsync(evt, context.CancellationToken);

            yield return newDirectory;
        }
    }

    public string? GoBack()
    {
        lock (_directoryStackGate)
        {
            if (_directoryStackIndex <= 0)
            {
                return null;
            }

            _directoryStackIndex--;
            SaveDirectoryStackUnsafe();
            return _directoryStack[_directoryStackIndex];
        }
    }

    public string? GoForward()
    {
        lock (_directoryStackGate)
        {
            if (_directoryStackIndex >= _directoryStack.Count - 1)
            {
                return null;
            }

            _directoryStackIndex++;
            SaveDirectoryStackUnsafe();
            return _directoryStack[_directoryStackIndex];
        }
    }

    public string? GoToStackIndex(int index)
    {
        lock (_directoryStackGate)
        {
            if (index < 0 || index >= _directoryStack.Count)
            {
                return null;
            }

            _directoryStackIndex = index;
            SaveDirectoryStackUnsafe();
            return _directoryStack[_directoryStackIndex];
        }
    }

    public IReadOnlyList<DirectoryStackEntry> GetDirectoryStack()
    {
        lock (_directoryStackGate)
        {
            var entries = new DirectoryStackEntry[_directoryStack.Count];

            for (var index = 0; index < _directoryStack.Count; index++)
            {
                entries[index] = new DirectoryStackEntry(index, _directoryStack[index], index == _directoryStackIndex);
            }

            return entries;
        }
    }

    public int DirectoryStackIndex
    {
        get { lock (_directoryStackGate) { return _directoryStackIndex; } }
    }

    public int DirectoryStackCount
    {
        get { lock (_directoryStackGate) { return _directoryStack.Count; } }
    }

    public bool RemoveDirectoryStackEntry(int index)
    {
        lock (_directoryStackGate)
        {
            if (index < 0 || index >= _directoryStack.Count)
            {
                return false;
            }

            if (index == _directoryStackIndex)
            {
                return false;
            }

            _directoryStack.RemoveAt(index);

            if (index < _directoryStackIndex)
            {
                _directoryStackIndex--;
            }

            SaveDirectoryStackUnsafe();
            return true;
        }
    }

    public void ClearDirectoryStack()
    {
        lock (_directoryStackGate)
        {
            var current = _directoryStackIndex >= 0 && _directoryStackIndex < _directoryStack.Count
                ? _directoryStack[_directoryStackIndex]
                : CurrentDirectory;

            _directoryStack.Clear();
            _directoryStack.Add(current);
            _directoryStackIndex = 0;
            SaveDirectoryStackUnsafe();
        }
    }

    private void SaveDirectoryStackUnsafe()
    {
        if (!_directoryStackStorageInitialized || _directoryStackFilePath is null)
        {
            return;
        }

        DirectoryStackFileStore.Save(_directoryStackFilePath, _directoryStack, _directoryStackIndex);
    }

    public void RequestExit()
    {
        ExitRequested = true;
    }

    /// <summary>
    /// Whether <paramref name="name"/> has been exported to the process environment
    /// (<see cref="IToastHostSignals"/>). The set itself stays shell-side; this is the
    /// membership test the language needs for `forget` (`TOAST-0006`).
    /// </summary>
    public bool IsExported(string name) => ExportedEnvironmentVariables.Contains(name);

    /// <summary>
    /// Renders a warning the language reported and writes it to the error stream
    /// (<see cref="IToastDiagnosticSink"/>). Theme and destination are the shell's, which
    /// is the point of the language not doing this itself (`TOAST-0006`).
    /// </summary>
    public void ReportWarning(ToshDiagnostic diagnostic)
        => Error.WriteLine(new DiagnosticRenderer(Config.Theme.Diagnostics, Config.Diagnostics)
            .RenderWarning(diagnostic));

    /// <inheritdoc cref="ReportWarning(ToshDiagnostic)"/>
    public void ReportWarning(string title, string? help, string? info)
        => Error.WriteLine(new DiagnosticRenderer(Config.Theme.Diagnostics, Config.Diagnostics)
            .RenderWarning(title, help, info));

    /// <summary>
    /// Writes a trace line to the error stream. Whether tracing is on at all is still the
    /// language's question — it knows what it is about to do; this decides where the line
    /// goes.
    /// </summary>
    public async ValueTask TraceAsync(string line, CancellationToken cancellationToken = default)
        => await Error.WriteLineAsync(line.AsMemory(), cancellationToken);

    public void SetLastExitCode(int exitCode)
    {
        LastExitCode = exitCode;
    }

    public void SetLastResult(object? value)
    {
        LastResult = value;
    }

    public void SetLastCommandDuration(TimeSpan? duration)
    {
        LastCommandDuration = duration;
    }

    /// <summary>
    /// Records the rendered diagnostic and underlying exception for the most recent
    /// command that failed. Pass <c>null</c> for both arguments after a successful
    /// command to clear the previous error.
    /// </summary>
    public void SetLastDiagnostic(string? rendered, Exception? error)
    {
        LastDiagnostic = string.IsNullOrEmpty(rendered) ? null : rendered;
        LastError = error;
    }

    /// <summary>
    /// Records the wall-clock start time of the most recent command. Typically called
    /// by the REPL just before invoking the engine.
    /// </summary>
    public void SetLastStartedAt(DateTimeOffset? startedAt)
    {
        LastStartedAt = startedAt;
    }

    public void RegisterDisplaySelection(object value, DisplayColumnSelection selection)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(selection);

        if (!selection.HasOverrides)
        {
            return;
        }

        lock (_displaySelectionGate)
        {
            _displaySelections[value] = selection;
        }
    }

    public DisplayColumnSelection? GetDisplaySelection(object? value)
    {
        if (value is null)
        {
            return null;
        }

        lock (_displaySelectionGate)
        {
            return _displaySelections.TryGetValue(value, out var selection) ? selection : null;
        }
    }

    public void ClearDisplaySelections()
    {
        lock (_displaySelectionGate)
        {
            _displaySelections.Clear();
        }
    }

    public void ExportEnvironmentVariable(string name, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ExportedEnvironmentVariables.Add(name);
        Variables[name] = value;
        Environment.SetEnvironmentVariable(name, ExternalTextSerializer.Serialize(value));
    }

    public void SyncExportedEnvironmentVariable(string name, object? value)
    {
        if (!ExportedEnvironmentVariables.Contains(name))
        {
            return;
        }

        Environment.SetEnvironmentVariable(name, ExternalTextSerializer.Serialize(value));
    }

    public void RemoveExportedEnvironmentVariable(string name)
    {
        ExportedEnvironmentVariables.Remove(name);
        Environment.SetEnvironmentVariable(name, null);
    }

    public static ToshRuntime CreateDefault(TextWriter? output = null, TextWriter? error = null)
    {
        // Kick off platform type index construction in the background so it is ready
        // before startup files (which may declare refinement types) are loaded.
        WarmUpTask = Task.Run(DotNetTypeResolver.WarmUpPlatformTypeIndex);

        EnsureStdlibLoaded();
        var runtime = new ToshRuntime(output, error);
        BuiltInShellTypes.RegisterDefaults(runtime.Classes);
        DefaultCommandRegistrar?.Invoke(runtime);
        return runtime;
    }

    private static readonly object s_stdlibLoadGate = new();
    private static int _stdlibLoadCompleted;

    /// <summary>
    /// Forces the Tosh.Stdlib assembly to load so its [ModuleInitializer] runs and
    /// installs the default command/profile registrars. Project references alone
    /// don't guarantee load — the runtime only resolves an assembly when IL
    /// references a type from it, and Tosh.Runtime deliberately doesn't reference any
    /// stdlib types. Tries once per process; concurrent callers wait until the
    /// module initializer has installed its registrars before proceeding. Safe
    /// if Tosh.Stdlib isn't deployed (embedding scenarios that bring their own
    /// command set).
    /// </summary>
    internal static void EnsureStdlibLoaded()
    {
        if (Volatile.Read(ref _stdlibLoadCompleted) != 0)
        {
            return;
        }

        lock (s_stdlibLoadGate)
        {
            if (Volatile.Read(ref _stdlibLoadCompleted) != 0)
            {
                return;
            }

            try
            {
                var assembly = System.Reflection.Assembly.Load(new System.Reflection.AssemblyName("Tosh.Stdlib"));
                // Assembly.Load only loads metadata; module initializers don't run until
                // a type from the module is first accessed. Force the [ModuleInitializer]
                // to fire so DefaultCommandRegistrar/DefaultProfileRegistrar get installed.
                System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(
                    assembly.ManifestModule.ModuleHandle);
            }
            catch (System.IO.FileNotFoundException)
            {
                // Tosh.Stdlib not deployed — caller registers via DefaultCommandRegistrar.
            }
            finally
            {
                Volatile.Write(ref _stdlibLoadCompleted, 1);
            }
        }
    }

    /// <summary>
    /// Pluggable hook used by Tosh.Stdlib (or other layered command packages) to
    /// register the built-in command set when a runtime is created via
    /// <see cref="CreateDefault"/>. Tosh.Runtime does not own any commands, so this
    /// stays null when only the runtime contract is loaded; Tosh.Stdlib wires it
    /// from a [ModuleInitializer].
    /// </summary>
    public static Action<ToshRuntime>? DefaultCommandRegistrar { get; set; }

    /// <summary>
    /// Task that pre-warms the platform type index in the background.
    /// Await this before processing startup files to ensure type resolution is fast.
    /// </summary>
    public static Task? WarmUpTask { get; private set; }

    private void ReloadHistoryUnsafe()
    {
        History.Clear();
        _nextHistoryId = 0;

        if (!Config.History.Persistent || Config.History.MaxEntries == 0)
        {
            return;
        }

        var entries = HistoryFileStore.Load(Config.History.FilePath);

        foreach (var entry in entries)
        {
            AddLoadedHistoryEntryUnsafe(entry);
        }
    }

    private void SaveHistoryUnsafe()
    {
        if (!_historyStorageInitialized || !Config.History.Persistent)
        {
            return;
        }

        HistoryFileStore.Save(Config.History.FilePath, History.ToArray());
    }

    private bool TryAddHistoryEntryUnsafe(string source, DateTimeOffset timestamp)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        if (Config.History.MaxEntries == 0)
        {
            return false;
        }

        if (Config.History.IgnoreLeadingSpace && char.IsWhiteSpace(source[0]))
        {
            return false;
        }

        switch (Config.History.Deduplication)
        {
            case ToshHistoryDeduplicationMode.Consecutive
                when History.Count > 0 &&
                     string.Equals(History[^1].Text, source, StringComparison.Ordinal):
                return false;
            case ToshHistoryDeduplicationMode.All:
                for (var index = History.Count - 1; index >= 0; index--)
                {
                    if (string.Equals(History[index].Text, source, StringComparison.Ordinal))
                    {
                        History.RemoveAt(index);
                    }
                }

                break;
        }

        var id = ++_nextHistoryId;
        History.Add(new CommandHistoryEntry(id, source, timestamp));
        TrimHistoryUnsafe();
        return true;
    }

    private void AddLoadedHistoryEntryUnsafe(CommandHistoryEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Text))
        {
            return;
        }

        _nextHistoryId = Math.Max(_nextHistoryId, entry.Id);
        History.Add(entry);
        TrimHistoryUnsafe();
    }

    private void TrimHistoryUnsafe()
    {
        if (Config.History.MaxEntries is not int maxEntries)
        {
            return;
        }

        var excess = History.Count - maxEntries;
        if (excess <= 0)
        {
            return;
        }

        if (History is List<CommandHistoryEntry> concrete)
        {
            concrete.RemoveRange(0, excess);
            return;
        }

        for (var i = 0; i < excess; i++)
        {
            History.RemoveAt(0);
        }
    }
}
