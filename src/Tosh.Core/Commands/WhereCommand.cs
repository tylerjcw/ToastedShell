namespace Tosh.Core.Commands;

public sealed class WhereCommand : ShellCommand
{
    public WhereCommand()
        : base("where", "Filters pipeline objects with a comparison, predicate expression, or predicate block.", "where <member-path> <operator> <value> or where <expression> or where { <predicate>; ... }") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count == 1 && context.Arguments[0] is ShellBlock predicateBlock)
        {
            await foreach (var item in ExecutePredicateBlockAsync(context, predicateBlock).WithCancellation(context.CancellationToken))
            {
                yield return item;
            }

            yield break;
        }

        var clauses = WherePredicateMatcher.GetClauses(context);
        var nullablePathCache = new Dictionary<(Type Type, string MemberPath), bool>();

        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            if (WherePredicateMatcher.MatchesAll(item, clauses, nullablePathCache, context.Runtime.ObjectAccessor))
            {
                yield return item;
            }
        }
    }

    private static async IAsyncEnumerable<object?> ExecutePredicateBlockAsync(
        CommandContext context,
        ShellBlock predicateBlock)
    {
        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            if (await PredicateBlockEvaluator.EvaluateAsync(context, predicateBlock, item))
            {
                yield return item;
            }
        }
    }
}
