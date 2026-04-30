using Tosh.Runtime;

namespace Tosh.Language.Parsing;

public abstract record PipelineStageSyntax(TextSpan Span);

public sealed record ExpressionPipelineStageSyntax(ArgumentSyntax Expression, TextSpan Span) : PipelineStageSyntax(Span);

public sealed record PipeForwardStageSyntax(CommandSyntax Command, TextSpan Span) : PipelineStageSyntax(Span);
