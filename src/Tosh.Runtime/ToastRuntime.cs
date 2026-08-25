namespace Tosh.Runtime;

/// <summary>
/// The state a Tōast program needs, independent of any shell.
/// </summary>
/// <remarks>
/// <para>
/// `TOAST-0006`, stage 2d. `ToshRuntime` carries 38 public members and the language
/// touches 23 of them; the other fifteen are display, terminal, session and
/// `$tosh.Last` state that Tōast never reads. This type holds the language's half, and
/// `ToshRuntime` composes one rather than inheriting from it.
/// </para>
/// <para>
/// **Composition rather than inheritance was chosen deliberately.** With a base class,
/// every member added later needs a judgement about which class it lands in — and that
/// judgement is exactly what produced `Config.Shell.MaxRecursionDepth`, a limit on the
/// evaluator filed under "Shell". Composition makes the question "does the language need
/// this?" rather than "which half does this feel like?", and only the first has an
/// answer that can be checked.
/// </para>
/// <para>
/// The test it exists to pass: a host constructs a `ToastRuntime` and nothing else, and
/// Tōast runs. That is the same test `TOSH-0003` sets for packaging — Tōast installed,
/// TōSh absent, a script still runs — and the reason `SELF_HOSTING_RFC.md` can describe
/// TōSh as a port target rather than a prerequisite.
/// </para>
/// <para>
/// The language-owned state and services now live here; <c>ToshRuntime</c> forwards the
/// same objects where the shell also consumes them. Formatting left through `TOAST-0014`,
/// output and redirection use language streams through `TOAST-0015`, and object dispatch
/// is supplied through host contracts rather than fixed reflection implementations.
/// </para>
/// </remarks>
public sealed class ToastRuntime
{
    private sealed class UnhostedServices :
        IToastHostSignals,
        IToastDiagnosticSink,
        IToastExecutionObserver,
        IToastSessionRedirection
    {
        internal static readonly UnhostedServices Instance = new();

        private sealed class NoopScope : IDisposable
        {
            internal static readonly NoopScope Instance = new();

            public void Dispose() { }
        }

        public bool ExitRequested => false;

        public void RequestExit() { }

        public bool IsExported(string name) => false;

        public void SyncExportedEnvironmentVariable(string name, object? value) { }

        public void RemoveExportedEnvironmentVariable(string name) { }

        public void ReportWarning(ToshDiagnostic diagnostic) { }

        public void ReportWarning(string title, string? help, string? info) { }

        public ValueTask TraceAsync(string line, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public void SetLastResult(object? value) { }

        public void SetLastExitCode(int exitCode) { }

        public IDisposable Begin(IToastStream? output, IToastStream? error)
            => NoopScope.Instance;
    }

    /// <summary>Where a program's ordinary output goes.</summary>
    /// <remarks>
    /// A destination the language owns, not the shell's <c>TextWriter</c> — `TOAST-0015`.
    /// Defaults to <see cref="ToastStreams.Null"/> so a host that supplies none can still
    /// run a program that writes: a `no_clr` program has no terminal, and "there is nowhere
    /// to write" is a legitimate configuration rather than an error.
    /// </remarks>
    public IToastStream Output { get; set; } = ToastStreams.Null;

    /// <summary>Where a program's diagnostics go.</summary>
    public IToastStream Error { get; set; } = ToastStreams.Null;

    /// <summary>Session signals observed or requested by the language.</summary>
    /// <remarks>
    /// A language-only host has no session to exit and no process environment exports, so
    /// the default answers false and treats exit requests as inert. TōSh supplies its live
    /// session implementation when composing this runtime (`TOAST-0006`, stage 2b).
    /// </remarks>
    public IToastHostSignals HostSignals { get; init; } = UnhostedServices.Instance;

    /// <summary>Where language warnings and trace events are reported.</summary>
    /// <remarks>
    /// Reporting defaults to an inert sink for an unhosted runtime. A host that presents
    /// diagnostics supplies its own renderer/destination without putting those shell choices
    /// back into the language (`TOAST-0006`, stage 2e).
    /// </remarks>
    public IToastDiagnosticSink Diagnostics { get; init; } = UnhostedServices.Instance;

    /// <summary>Receives result and exit-code observations for host session state.</summary>
    public IToastExecutionObserver ExecutionObserver { get; init; } = UnhostedServices.Instance;

    /// <summary>Mirrors language redirection into an optional host session.</summary>
    /// <remarks>
    /// The default scope is inert because an embedded language host need not have a
    /// session. TōSh supplies its live implementation so shell commands and external
    /// processes follow the language's redirected destinations without the evaluator
    /// reaching into <c>ToshRuntime.Output</c> or <c>ToshRuntime.Error</c>.
    /// </remarks>
    public IToastSessionRedirection SessionRedirection { get; init; } = UnhostedServices.Instance;

    /// <summary>
    /// Creates commands that launch external programs, or <see langword="null"/> when the
    /// host does not expose process execution.
    /// </summary>
    /// <remarks>
    /// Process launching is a host capability rather than language state. Keeping the
    /// optional factory here lets command resolution decline honestly in an embedded host
    /// without making the evaluator depend on TōSh's process implementation (`TOAST-0004`,
    /// `TOAST-0006`).
    /// </remarks>
    public IExternalCommandFactory? ExternalCommands { get; set; }

    /// <summary>Arguments supplied to the current top-level script invocation.</summary>
    public IReadOnlyList<object?> InvocationArguments { get; set; } = Array.Empty<object?>();

    /// <summary>Executes a language block on behalf of a host-provided command.</summary>
    public IShellBlockExecutor? BlockExecutor { get; set; }

    /// <summary>Exposes the active evaluator to host-provided commands.</summary>
    public IShellEvaluator? Evaluator { get; set; }

    /// <summary>Constructs values and invokes members through the host's object model.</summary>
    /// <remarks>
    /// The .NET host uses <see cref="ReflectionInvoker"/>. The contract and init-only
    /// substitution point keep reflection out of the runtime shape a native host must
    /// implement (`TOAST-0006`, stage 2d).
    /// </remarks>
    public IObjectInvoker Invoker { get; init; } = new ReflectionInvoker();

    /// <summary>Reads members off values — an interface, so a native target can replace it.</summary>
    public IObjectAccessor ObjectAccessor { get; init; } = new ReflectionObjectAccessor();

    /// <summary>Resolves type names — an interface, for the same reason.</summary>
    public ITypeResolver TypeResolver { get; init; } = new DotNetTypeResolver();

    /// <summary>Settings the language owns, independent of any shell config file.</summary>
    public ToastOptions Options { get; } = new();

    /// <summary>
    /// The command table: names the language can resolve, and the two mutations it needs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Typed as <see cref="ICommandTable"/> — six members — rather than the shell's
    /// eleven-member `ShellCommandRegistry`, so `RegisterAlias`, `GetAliases`, `Get` and
    /// `Register` stay shell-only (`TOAST-0006`, stage 2a).
    /// </para>
    /// <para>
    /// It is **shared, not moved**. TōSh creates the registry and hands the same instance
    /// here, because both halves must see one table: `export func greet()` registers
    /// through the language and `which greet` resolves through the shell. A default is
    /// supplied so a language-only host still has somewhere for declarations to go.
    /// </para>
    /// </remarks>
    public ICommandTable Commands { get; init; } = new ShellCommandRegistry();

    /// <summary>
    /// The working directory relative paths resolve against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Held here rather than threaded per evaluation, which was the original decision and
    /// is deferred rather than abandoned (`TOAST-0006`, stage 2f). Threading it would mean
    /// adding a parameter to a dozen synchronous helpers deep in the engine — none of the
    /// fifteen call sites has a context parameter and only two have a
    /// <see cref="CancellationToken"/> — for a benefit, concurrent evaluations with
    /// different working directories, that nothing exercises yet.
    /// </para>
    /// <para>
    /// The language reads this for path resolution and glob expansion. TōSh keeps the
    /// process directory in step and owns the navigation *stack* that `back` and
    /// `forward` use, which is history rather than state a program needs.
    /// </para>
    /// </remarks>
    public string CurrentDirectory { get; set; } = ResolveInitialDirectory();

    private static string ResolveInitialDirectory()
    {
        try
        {
            return Environment.CurrentDirectory;
        }
        catch (Exception)
        {
            // A deleted or unreadable working directory is not a reason to fail
            // construction; the home directory is somewhere paths can resolve from.
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
    }

    /// <summary>Global variables.</summary>
    public IDictionary<string, object?> Variables { get; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>Declared classes, records, traits and interfaces, by name.</summary>
    public IDictionary<string, object?> Classes { get; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>Declared modules, by name.</summary>
    public IDictionary<string, object?> Modules { get; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>Modules already loaded, so `require` is idempotent per session.</summary>
    public ISet<string> LoadedModules { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// CLR types emitted for globally-declared <c>raw struct</c>s, keyed by declared name,
    /// so a global raw struct is nameable in a native signature.
    /// </summary>
    public IDictionary<string, Type> NativeTypes { get; } =
        new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The event bus. `event` is language syntax, and the language does not merely raise
    /// on it — it registers handlers and marks events required — so the bus belongs here
    /// rather than being a port the language raises through (`TOAST-0006`, stage 2c).
    /// </summary>
    /// <remarks>
    /// The six types behind it — `ShellEventBus`, `ShellEvent`, `ShellEventSender`,
    /// `ShellEventHandler`, `IShellEventFactory` and `BuiltInEvents`, 663 lines — name no
    /// shell-side type between them, so nothing had to be untangled for this to move.
    /// TōSh and the standard library remain consumers, which is the ordinary direction:
    /// a shell subscribes to its language's events.
    /// </remarks>
    public ShellEventBus Events { get; } = new();

    /// <summary>
    /// Builds the sender attached to a raised event. Supplied by the engine, which knows
    /// how to give a handler a way to call back into evaluation.
    /// </summary>
    public Func<ShellEventSender>? EventSenderFactory { get; set; }
}
