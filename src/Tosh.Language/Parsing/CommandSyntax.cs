using Tosh.Runtime;

namespace Tosh.Language.Parsing;

public sealed record CommandSyntax(
    string Name,
    TextSpan NameSpan,
    IReadOnlyList<ArgumentSyntax> Arguments,
    TextSpan Span) : PipelineStageSyntax(Span);
