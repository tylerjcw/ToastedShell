using Tosh.Core;

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
}
