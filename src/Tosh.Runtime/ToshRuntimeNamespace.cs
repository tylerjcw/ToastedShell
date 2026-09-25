using System.Reflection;
using System.Runtime.InteropServices;

namespace Tosh.Runtime;

internal sealed class ToshRuntimeNamespace
    : IShellRecordObject, IShellRuntimeNamespaceSummarySource
{
    private readonly ToshRuntime _runtime;

    public ToshRuntimeNamespace(
        ToshRuntime runtime,
        IToastScriptNamespace script,
        IToastFunctionNamespace function)
    {
        _runtime = runtime;
        Last = new ToshLastNamespace(runtime);
        Script = script;
        Function = function;
        Session = new ToshSessionNamespace(runtime);
        Host = new ToshHostNamespace();
    }

    public ToshConfig Config => _runtime.Config;

    /// <summary>The terminal TōSh is talking to, and what it can be asked to do.</summary>
    /// <remarks>
    /// Detected once, when the shell starts, because the environment that names a terminal
    /// does not change under a running process. Anything the reader set in
    /// <c>$tosh.Config.Terminal</c> is laid over it, so a setting wins and an unset one is
    /// worked out.
    /// </remarks>
    public TerminalProfile Terminal => Config.ResolvedTerminal;

    public bool IsLoginShell => _runtime.IsLoginShell;

    public ToshLastNamespace Last { get; }

    public IToastScriptNamespace Script { get; }

    public IToastFunctionNamespace Function { get; }

    public ToshSessionNamespace Session { get; }

    public ToshHostNamespace Host { get; }

    public string ShellTypeName => "ToshRuntime";

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        switch (name)
        {
            case nameof(Config):
                value = Config;
                return true;
            case nameof(Terminal):
                value = Terminal;
                return true;
            case nameof(IsLoginShell):
                value = IsLoginShell;
                return true;
            case nameof(Last):
                value = Last;
                return true;
            case nameof(Script):
                value = Script;
                return true;
            case nameof(Function):
                value = Function;
                return true;
            case nameof(Session):
                value = Session;
                return true;
            case nameof(Host):
                value = Host;
                return true;
            default:
                value = null;
                return false;
        }
    }

    public bool TrySetMember(string name, object? value) => false;

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
        =>
        [
            new(nameof(Config), Config),
            new(nameof(Terminal), Terminal),
            new(nameof(IsLoginShell), IsLoginShell),
            new(nameof(Last), Last),
            new(nameof(Script), Script),
            new(nameof(Function), Function),
            new(nameof(Session), Session),
            new(nameof(Host), Host),
        ];

    public RuntimeNamespaceDisplaySummary GetDisplaySummary()
    {
        var topLevel = new List<(string, string)>
        {
            ("$tosh.IsLoginShell", IsLoginShell ? "True" : "False"),
        };

        var sections = new List<RuntimeNamespaceSection>
        {
            new(
                "$tosh.Host",
                "Host Namespace",
                [
                    ("Version", Host.Version),
                    ("RuntimeId", Host.RuntimeId),
                    ("Framework", Host.Framework),
                    ("ProcessId", Host.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("BuildSha256", FormatShortBuildSha(Host.BuildSha256)),
                ]),
            new(
                "$tosh.Session",
                "Session Namespace",
                [
                    ("CurrentDirectory", Session.CurrentDirectory),
                    ("JobCount", Session.JobCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("OpenHandleCount", Session.OpenHandleCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("NextHistoryId", Session.NextHistoryId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ]),
            new(
                "$tosh.Last",
                "Last Command Namespace",
                [
                    ("ExitCode", Last.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("Duration", Last.Duration?.ToString() ?? "n/a"),
                    ("HasError", Last.HasError ? "True" : "False"),
                ]),
            new(
                "$tosh.Config",
                "Configuration Namespace",
                [
                    ("Config Dir", Config.Startup.RootDirectory),
                    ("Hist. File", Config.History.FilePath),
                    ("Hist. Max", Config.History.MaxEntries?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unlimited"),
                    ("Hist Dedup.", Config.History.Deduplication.ToString()),
                ]),
        };

        var hasScript = !string.IsNullOrEmpty(Script.Path);
        if (hasScript)
        {
            sections.Add(new RuntimeNamespaceSection(
                "$tosh.Script",
                "Script Namespace",
                [
                    ("Path", Script.Path),
                    ("Name", Script.Name),
                    ("Directory", Script.Directory),
                    ("Args", Script.Args.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ]));
        }

        var hasFunction = !string.IsNullOrEmpty(Function.Name);
        if (hasFunction)
        {
            sections.Add(new RuntimeNamespaceSection(
                "$tosh.Function",
                "Function Namespace",
                [
                    ("Name", Function.Name),
                    ("Args", Function.Args.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ]));
        }

        var footnotes = new List<string>();
        if (!hasScript)
        {
            footnotes.Add("$tosh.Script — only available inside a script context.");
        }
        if (!hasFunction)
        {
            footnotes.Add("$tosh.Function — only available inside a function context.");
        }
        footnotes.Add("Use '$tosh.<Member>' to drill in, or '$tosh | to json' for a full snapshot.");

        return new RuntimeNamespaceDisplaySummary(
            "$tosh | TōSh Live Runtime Namespace",
            ShellTypeName,
            topLevel,
            sections,
            footnotes);
    }

    private static string FormatShortBuildSha(string sha)
        => string.IsNullOrEmpty(sha) ? "(unknown)" : sha[..Math.Min(12, sha.Length)];
}

internal sealed class ToshLastNamespace(ToshRuntime runtime) : IShellRecordObject
{
    public object? Result => runtime.LastResult;

    public int ExitCode => runtime.LastExitCode;

    public TimeSpan? Duration => runtime.LastCommandDuration;

    public string? Diagnostic => runtime.LastDiagnostic;

    public Exception? Error => runtime.LastError;

    public bool HasError => runtime.LastError is not null;

    public DateTimeOffset? StartedAt => runtime.LastStartedAt;

    public string ShellTypeName => "ToshRuntime.Last";

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        switch (name)
        {
            case nameof(Result):
                value = Result;
                return true;
            case nameof(ExitCode):
                value = ExitCode;
                return true;
            case nameof(Duration):
                value = Duration;
                return true;
            case nameof(Diagnostic):
                value = Diagnostic;
                return true;
            case nameof(Error):
                value = Error;
                return true;
            case nameof(HasError):
                value = HasError;
                return true;
            case nameof(StartedAt):
                value = StartedAt;
                return true;
            default:
                value = null;
                return false;
        }
    }

    public bool TrySetMember(string name, object? value) => false;

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
        =>
        [
            new(nameof(Result), Result),
            new(nameof(ExitCode), ExitCode),
            new(nameof(Duration), Duration),
            new(nameof(Diagnostic), Diagnostic),
            new(nameof(Error), Error),
            new(nameof(HasError), HasError),
            new(nameof(StartedAt), StartedAt),
        ];
}

internal sealed class ToshSessionNamespace(ToshRuntime runtime) : IShellRecordObject
{
    public string CurrentDirectory => runtime.CurrentDirectory;

    public int HistoryCount => runtime.History.Count;

    public long NextHistoryId => runtime.NextHistoryId;

    public string HistoryFilePath => runtime.Config.History.FilePath;

    public int JobCount => runtime.GetJobs().Count;

    /// <summary>The background jobs this session started.</summary>
    /// <remarks>
    /// The same list <c>jobs</c> reports, as a value. `JobCount` was here without any way to
    /// see what it counted.
    /// </remarks>
    public object[] Jobs => [.. runtime.GetJobs()];

    public int OpenHandleCount => ManagedFileHandle.GetOpenHandles().Count;

    public ManagedFileHandle[] OpenHandles => ManagedFileHandle.GetOpenHandles().ToArray();

    public StartupProfileData? StartupProfile => runtime.StartupProfile;

    public string ShellTypeName => "ToshRuntime.Session";

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        switch (name)
        {
            case nameof(Jobs):
                value = Jobs;
                return true;
            case nameof(CurrentDirectory):
                value = CurrentDirectory;
                return true;
            case nameof(HistoryCount):
                value = HistoryCount;
                return true;
            case nameof(NextHistoryId):
                value = NextHistoryId;
                return true;
            case nameof(HistoryFilePath):
                value = HistoryFilePath;
                return true;
            case nameof(JobCount):
                value = JobCount;
                return true;
            case nameof(OpenHandleCount):
                value = OpenHandleCount;
                return true;
            case nameof(OpenHandles):
                value = OpenHandles;
                return true;
            case nameof(StartupProfile):
                value = StartupProfile;
                return true;
            default:
                value = null;
                return false;
        }
    }

    public bool TrySetMember(string name, object? value) => false;

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
        =>
        [
            new(nameof(CurrentDirectory), CurrentDirectory),
            new(nameof(HistoryCount), HistoryCount),
            new(nameof(NextHistoryId), NextHistoryId),
            new(nameof(HistoryFilePath), HistoryFilePath),
            new(nameof(JobCount), JobCount),
            new(nameof(OpenHandleCount), OpenHandleCount),
            new(nameof(StartupProfile), StartupProfile),
            new(nameof(OpenHandles), OpenHandles),
            new(nameof(Jobs), Jobs),
        ];
}

internal sealed class ToshHostNamespace : IShellRecordObject
{
    private static string? _cachedBuildSha;

    public string Version => Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
                             ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                             ?? "0.0.0";

    public string RuntimeId => RuntimeInformation.RuntimeIdentifier;

    public string Framework => RuntimeInformation.FrameworkDescription;

    public string OSDescription => RuntimeInformation.OSDescription;

    public int ProcessId => Environment.ProcessId;

    public string ExecutablePath => Environment.ProcessPath ?? string.Empty;

    public bool IsInteractive => !Console.IsInputRedirected;

    /// <summary>Whether standard input comes from somewhere other than the terminal.</summary>
    public bool StdinRedirected => Console.IsInputRedirected;

    /// <summary>Whether standard output goes somewhere other than the terminal.</summary>
    /// <remarks>
    /// The question a script asks before drawing: a pipeline and a redirect both want plain
    /// text, and a full-screen interface has nowhere to draw in either.
    /// </remarks>
    public bool StdoutRedirected => Console.IsOutputRedirected;

    /// <summary>Whether standard error goes somewhere other than the terminal.</summary>
    public bool StderrRedirected => Console.IsErrorRedirected;

    /// <summary>The effective user id — what the process is allowed to do, not who started it.</summary>
    public long UserId => UnixSystemServices.GetCurrentIdentity().Euid;

    /// <summary>The account name TōSh is running as.</summary>
    public string UserName => UnixSystemServices.GetCurrentIdentity().UserName;

    /// <summary>Whether TōSh is running with administrative rights.</summary>
    /// <remarks>
    /// What a script checks before reaching for <c>sudo</c>, rather than shelling out to
    /// <c>id -u</c> to find out. On Windows the identity carries the same value for both ids,
    /// so this is the Unix question and answers false there.
    /// </remarks>
    public bool IsElevated => !OperatingSystem.IsWindows() && UserId == 0;

    public string BuildSha256
    {
        get
        {
            if (_cachedBuildSha is not null) return _cachedBuildSha;
            try
            {
                var path = Environment.ProcessPath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    _cachedBuildSha = string.Empty;
                    return _cachedBuildSha;
                }
                using var stream = File.OpenRead(path);
                using var sha = System.Security.Cryptography.SHA256.Create();
                var bytes = sha.ComputeHash(stream);
                _cachedBuildSha = Convert.ToHexString(bytes).ToLowerInvariant();
            }
            catch
            {
                _cachedBuildSha = string.Empty;
            }
            return _cachedBuildSha;
        }
    }

    public string ShellTypeName => "ToshRuntime.Host";

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        switch (name)
        {
            case nameof(StdinRedirected):
                value = StdinRedirected;
                return true;
            case nameof(StdoutRedirected):
                value = StdoutRedirected;
                return true;
            case nameof(StderrRedirected):
                value = StderrRedirected;
                return true;
            case nameof(UserId):
                value = UserId;
                return true;
            case nameof(UserName):
                value = UserName;
                return true;
            case nameof(IsElevated):
                value = IsElevated;
                return true;
            case nameof(Version):
                value = Version;
                return true;
            case nameof(RuntimeId):
                value = RuntimeId;
                return true;
            case nameof(Framework):
                value = Framework;
                return true;
            case nameof(OSDescription):
                value = OSDescription;
                return true;
            case nameof(ProcessId):
                value = ProcessId;
                return true;
            case nameof(ExecutablePath):
                value = ExecutablePath;
                return true;
            case nameof(IsInteractive):
                value = IsInteractive;
                return true;
            case nameof(BuildSha256):
                value = BuildSha256;
                return true;
            default:
                value = null;
                return false;
        }
    }

    public bool TrySetMember(string name, object? value) => false;

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
        =>
        [
            new(nameof(Version), Version),
            new(nameof(RuntimeId), RuntimeId),
            new(nameof(Framework), Framework),
            new(nameof(OSDescription), OSDescription),
            new(nameof(ProcessId), ProcessId),
            new(nameof(ExecutablePath), ExecutablePath),
            new(nameof(IsInteractive), IsInteractive),
            new(nameof(BuildSha256), BuildSha256),
            new(nameof(StdinRedirected), StdinRedirected),
            new(nameof(StdoutRedirected), StdoutRedirected),
            new(nameof(StderrRedirected), StderrRedirected),
            new(nameof(UserId), UserId),
            new(nameof(UserName), UserName),
            new(nameof(IsElevated), IsElevated),
        ];
}
