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
public sealed record CommandContext(
    ToshRuntime Runtime,
    IAsyncEnumerable<object?> Input,
    IReadOnlyList<object?> Arguments,
    CancellationToken CancellationToken,
    CommandInvocation? Invocation = null,
    bool IsPipelined = false,
    ITypeResolver? ScopedTypeResolver = null,
    PipelineExitStatusTracker? PipelineExitStatusTracker = null,
    IShellBlockExecutor? BlockExecutor = null,
    bool OutputIsCaptured = false)
{
    public ITypeResolver TypeResolver => ScopedTypeResolver ?? Runtime.TypeResolver;

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
