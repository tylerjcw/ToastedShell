namespace Tosh.Core;

internal static class WherePredicateMatcher
{
    public static IReadOnlyList<WherePredicateClause> GetClauses(CommandContext context)
    {
        if (context.Arguments.Count == 1 && context.Arguments[0] is WherePredicateBlock block)
        {
            ValidateClauses(context, block.Clauses);
            return block.Clauses;
        }

        var rawMemberPath = CommandArguments.RequireString(context.Arguments, 0, "member path");
        var @operator = CommandArguments.RequireString(context.Arguments, 1, "operator");

        if (context.Arguments.Count < 3)
        {
            throw new InvalidOperationException("Missing required argument: value.");
        }

        if (@operator == "=")
        {
            throw context.CreateDiagnostic(
                code: "tosh::parser::assignment_requires_variable",
                title: "Assignment operations require a variable.",
                argumentIndex: 1,
                label: "use '==' for equality comparisons in 'where'",
                help: $"try `where {rawMemberPath} == {context.Arguments.ElementAtOrDefault(2) ?? "..."}`");
        }

        var firstSpan = context.GetArgumentSpan(0);
        var thirdSpan = context.GetArgumentSpan(2);

        return
        [
            new WherePredicateClause(
                rawMemberPath,
                @operator,
                context.Arguments[2],
                firstSpan is TextSpan startSpan && thirdSpan is TextSpan endSpan
                    ? TextSpan.FromBounds(startSpan.Start, endSpan.End)
                    : firstSpan ?? default,
                context.GetArgumentSpan(1) ?? default),
        ];
    }

    public static bool MatchesAll(
        object? item,
        IReadOnlyList<WherePredicateClause> clauses,
        IDictionary<(Type Type, string MemberPath), bool> nullablePathCache,
        IObjectAccessor accessor)
    {
        foreach (var clause in clauses)
        {
            var memberPath = MemberPath.Parse(clause.MemberPath);
            var actual = accessor.GetValue(item, clause.MemberPath);
            var nullable = memberPath.IsNullable || IsStaticallyNullablePath(item, clause.MemberPath, nullablePathCache, accessor);

            if (!OperatorEvaluator.Matches(actual, clause.Operator, clause.Expected, nullable))
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateClauses(CommandContext context, IReadOnlyList<WherePredicateClause> clauses)
    {
        foreach (var clause in clauses)
        {
            if (clause.Operator == "=")
            {
                throw context.CreateDiagnostic(
                    code: "tosh::parser::assignment_requires_variable",
                    title: "Assignment operations require a variable.",
                    label: "use '==' for equality comparisons in 'where'",
                    help: $"try `where {clause.MemberPath} == ...`",
                    span: clause.OperatorSpan);
            }
        }
    }

    private static bool IsStaticallyNullablePath(
        object? item,
        string memberPath,
        IDictionary<(Type Type, string MemberPath), bool> cache,
        IObjectAccessor accessor)
    {
        if (item is null)
        {
            return false;
        }

        var key = (item.GetType(), memberPath);

        if (cache.TryGetValue(key, out var isNullable))
        {
            return isNullable;
        }

        isNullable = accessor.IsNullablePath(key.Item1, memberPath);
        cache[key] = isNullable;
        return isNullable;
    }
}
