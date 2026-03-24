using Tosh.Core;

namespace Tosh.Language.Parsing;

public abstract record PipelineStageSyntax(TextSpan Span);

public sealed record ExpressionPipelineStageSyntax(ArgumentSyntax Expression, TextSpan Span) : PipelineStageSyntax(Span);
