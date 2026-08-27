namespace Tosh.Runtime;

public sealed record ShellBlock(
    object Syntax,
    string SourceName,
    string SourceText,
    TextSpan Span)
{
    /// <summary>
    /// Optional captured-variable bindings recorded by the IL emitter
    /// when materializing a <see cref="BoundBlockExpression"/>. The
    /// <see cref="EngineBlockExecutor"/> overlays these on the locals
    /// dictionary the host command provides so identifiers like
    /// <c>$threshold</c> resolve when the block re-enters the engine.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Captures { get; init; }
}
