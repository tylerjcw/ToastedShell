namespace Tosh.Core.Commands;

public sealed class TakeWhileCommand : ShellCommand
{
    public TakeWhileCommand()
        : base("take-while", "Yields input values while the predicate remains true.", "take-while { <predicate> }") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var block = context.Arguments.Count == 1 ? context.Arguments[0] as ShellBlock : null;
        var hasPredicateBlock = block is not null;
        var clauses = hasPredicateBlock ? null : WherePredicateMatcher.GetClauses(context);
        var nullablePathCache = new Dictionary<(Type Type, string MemberPath), bool>();

        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            var matches = hasPredicateBlock
                ? await PredicateBlockEvaluator.EvaluateAsync(context, block!, item)
                : WherePredicateMatcher.MatchesAll(item, clauses!, nullablePathCache, context.Runtime.ObjectAccessor);

            if (!matches)
            {
                yield break;
            }

            yield return item;
        }
    }
}
