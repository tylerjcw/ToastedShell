namespace Tosh.Core;

public sealed record WherePredicateClause(
    string MemberPath,
    string Operator,
    object? Expected,
    TextSpan Span,
    TextSpan OperatorSpan);
