using Tosh.Core;

namespace Tosh.Language.Parsing;

public abstract record ArgumentSyntax(TextSpan Span);

public sealed record BarewordArgumentSyntax(string Value, TextSpan Span) : ArgumentSyntax(Span);

public sealed record LiteralArgumentSyntax(object? Value, TextSpan Span) : ArgumentSyntax(Span);

public sealed record VariableReferenceArgumentSyntax(string Name, TextSpan Span) : ArgumentSyntax(Span);

public sealed record NewObjectArgumentSyntax(string TypeName, IReadOnlyList<ArgumentSyntax> Arguments, TextSpan Span) : ArgumentSyntax(Span);

public sealed record StaticMethodCallArgumentSyntax(
    string Path,
    IReadOnlyList<ArgumentSyntax> Arguments,
    TextSpan Span) : ArgumentSyntax(Span);

public sealed record StaticMemberAccessArgumentSyntax(string Path, TextSpan Span) : ArgumentSyntax(Span);

public sealed record ListLiteralArgumentSyntax(IReadOnlyList<ArgumentSyntax> Items, TextSpan Span) : ArgumentSyntax(Span);

public sealed record BlockArgumentSyntax(BlockSyntax Block, TextSpan Span) : ArgumentSyntax(Span);

public sealed record MemberProjectionArgumentSyntax(IReadOnlyList<string> MemberPaths, TextSpan Span) : ArgumentSyntax(Span);

public sealed record MemberAccessArgumentSyntax(ArgumentSyntax Target, string MemberPath, TextSpan Span) : ArgumentSyntax(Span);

public sealed record MethodCallArgumentSyntax(
    ArgumentSyntax Target,
    string MethodName,
    IReadOnlyList<ArgumentSyntax> Arguments,
    TextSpan Span) : ArgumentSyntax(Span);

public sealed record SubexpressionArgumentSyntax(PipelineSyntax Pipeline, TextSpan Span) : ArgumentSyntax(Span);

public sealed record OperatorArgumentSyntax(
    ArgumentSyntax Left,
    string Operator,
    TextSpan OperatorSpan,
    ArgumentSyntax Right,
    TextSpan Span) : ArgumentSyntax(Span);

public sealed record PredicateBlockArgumentSyntax(IReadOnlyList<PredicateClauseSyntax> Clauses, TextSpan Span) : ArgumentSyntax(Span);

public sealed record PredicateClauseSyntax(
    string MemberPath,
    TextSpan MemberPathSpan,
    string Operator,
    TextSpan OperatorSpan,
    ArgumentSyntax Expected,
    TextSpan Span);
