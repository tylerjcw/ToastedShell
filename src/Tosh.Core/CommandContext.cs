namespace Tosh.Core;

public sealed record CommandContext(
    ToshRuntime Runtime,
    IAsyncEnumerable<object?> Input,
    IReadOnlyList<object?> Arguments,
    CancellationToken CancellationToken,
    CommandInvocation? Invocation = null,
    bool IsPipelined = false,
    ITypeResolver? ScopedTypeResolver = null,
    PipelineExitStatusTracker? PipelineExitStatusTracker = null)
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
