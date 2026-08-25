namespace Tosh.Runtime;

/// <summary>
/// What a command needs to know about the invocation it is serving.
/// </summary>
/// <remarks>
/// <see cref="OutputIsCaptured"/> is deliberately separate from
/// <see cref="IsPipelined"/>. They answer different questions — a pipelined command has an
/// input stage, a captured one has a *consumer* — and other code reads
/// <see cref="IsPipelined"/> to decide about input. Conflating them was the defect:
/// `ExternalProcessCommand` asked "is my output consumed?" and got `IsPipelined`, which is
/// true only when a downstream stage exists, so `var x = git …` at a terminal printed its
/// output and captured nothing (<c>TS-P1-30</c>).
/// </remarks>
public sealed record CommandContext
{
    public CommandContext(
        ToshRuntime Runtime,
        IAsyncEnumerable<object?> Input,
        IReadOnlyList<object?> Arguments,
        CancellationToken CancellationToken,
        CommandInvocation? Invocation = null,
        bool IsPipelined = false,
        ITypeResolver? ScopedTypeResolver = null,
        PipelineExitStatusTracker? PipelineExitStatusTracker = null,
        IShellBlockExecutor? BlockExecutor = null,
        bool OutputIsCaptured = false,
        IScopedCommandView? ScopedCommands = null,
        IShellNamedTypeView? ShellTypes = null)
        : this(
            Runtime.Language,
            Input,
            Arguments,
            CancellationToken,
            Invocation,
            IsPipelined,
            ScopedTypeResolver,
            PipelineExitStatusTracker,
            BlockExecutor,
            OutputIsCaptured,
            ScopedCommands,
            ShellTypes,
            Runtime)
    {
    }

    public CommandContext(
        ToastRuntime LanguageRuntime,
        IAsyncEnumerable<object?> Input,
        IReadOnlyList<object?> Arguments,
        CancellationToken CancellationToken,
        CommandInvocation? Invocation = null,
        bool IsPipelined = false,
        ITypeResolver? ScopedTypeResolver = null,
        PipelineExitStatusTracker? PipelineExitStatusTracker = null,
        IShellBlockExecutor? BlockExecutor = null,
        bool OutputIsCaptured = false,
        IScopedCommandView? ScopedCommands = null,
        IShellNamedTypeView? ShellTypes = null,
        ToshRuntime? ShellRuntime = null)
    {
        this.LanguageRuntime = LanguageRuntime ?? throw new ArgumentNullException(nameof(LanguageRuntime));
        this.Input = Input ?? throw new ArgumentNullException(nameof(Input));
        this.Arguments = Arguments ?? throw new ArgumentNullException(nameof(Arguments));
        this.CancellationToken = CancellationToken;
        this.Invocation = Invocation;
        this.IsPipelined = IsPipelined;
        this.ScopedTypeResolver = ScopedTypeResolver;
        this.PipelineExitStatusTracker = PipelineExitStatusTracker;
        this.BlockExecutor = BlockExecutor;
        this.OutputIsCaptured = OutputIsCaptured;
        this.ScopedCommands = ScopedCommands;
        this.ShellTypes = ShellTypes;
        this.ShellRuntime = ShellRuntime;
    }

    public ToastRuntime LanguageRuntime { get; init; }

    public ToshRuntime? ShellRuntime { get; init; }

    public ToshRuntime Runtime => ShellRuntime ?? throw new InvalidOperationException(
        "This command context has no TōSh session runtime. The command requires a shell host capability.");

    public IAsyncEnumerable<object?> Input { get; init; }

    public IReadOnlyList<object?> Arguments { get; init; }

    public CancellationToken CancellationToken { get; init; }

    public CommandInvocation? Invocation { get; init; }

    public bool IsPipelined { get; init; }

    public ITypeResolver? ScopedTypeResolver { get; init; }

    public PipelineExitStatusTracker? PipelineExitStatusTracker { get; init; }

    public IShellBlockExecutor? BlockExecutor { get; init; }

    public bool OutputIsCaptured { get; init; }

    public IScopedCommandView? ScopedCommands { get; init; }

    public IShellNamedTypeView? ShellTypes { get; init; }

    public ITypeResolver TypeResolver => ScopedTypeResolver ?? LanguageRuntime.TypeResolver;

    /// <summary>
    /// The commands an introspecting command should be able to see: the caller's lexical scopes
    /// when there are any, and the global registry otherwise.
    /// </summary>
    /// <remarks>
    /// Reach for this rather than <c>LanguageRuntime.Commands</c> in anything that reports on commands —
    /// <c>help</c>, <c>which</c>, completion — or it will not see a function the running script
    /// declared. See <see cref="IScopedCommandView"/> for why one is invisible to the other.
    /// </remarks>
    public IScopedCommandView VisibleCommands => ScopedCommands ?? LanguageRuntime.Commands;

    public TextSpan? GetArgumentSpan(int index) => Invocation?.GetArgumentSpan(index);

    public ToshDiagnosticException CreateDiagnostic(
        string code,
        string title,
        int? argumentIndex = null,
        string? label = null,
        string? help = null,
        TextSpan? span = null)
    {
        var diagnosticSpan = span ?? (argumentIndex is int index ? GetArgumentSpan(index) : null) ?? Invocation?.CommandSpan;

        return ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: code,
            Title: title,
            SourceName: Invocation?.SourceName,
            SourceText: Invocation?.SourceText,
            Span: diagnosticSpan,
            Label: label,
            Help: help));
    }
}
