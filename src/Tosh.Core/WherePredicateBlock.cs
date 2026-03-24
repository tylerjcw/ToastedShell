namespace Tosh.Core;

public sealed record WherePredicateBlock(IReadOnlyList<WherePredicateClause> Clauses, TextSpan Span);
