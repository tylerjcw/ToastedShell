using Tosh.Runtime;

namespace Tosh.Language.Parsing;

public enum RedirectionStream
{
    Output,          // o> / out>
    Error,           // e> / err>
    OutputThenError, // o+e> / out+err>
    ErrorThenOutput, // e+o> / err+out>
}

public enum RedirectionMode
{
    Truncate,   // >
    Append,     // >>
}

public sealed record RedirectionSyntax(
    RedirectionStream Stream,
    RedirectionMode Mode,
    ArgumentSyntax Target,
    TextSpan Span);

public sealed record InputRedirectionSyntax(
    ArgumentSyntax Source,
    TextSpan Span);

public sealed record PipelineSyntax(
    IReadOnlyList<PipelineStageSyntax> Stages,
    IReadOnlyList<RedirectionSyntax>? Redirections = null,
    InputRedirectionSyntax? InputRedirection = null,
    bool IsBackground = false)
{
    public IReadOnlyList<CommandSyntax> Commands => Stages.OfType<CommandSyntax>().ToArray();

    /// <summary>
    /// Set by the lowering pass when a recognised stage pattern (e.g.
    /// <c>sort | first N</c>) can be replaced by a specialised iterator.
    /// Body-declared so it does not participate in record equality.
    /// </summary>
    public Tosh.Language.Binding.PipelineFusion? Fusion { get; set; }
}
