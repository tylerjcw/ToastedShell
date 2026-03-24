namespace Tosh.Core.Commands;

public sealed class SkipWhileCommand : ShellCommand
{
    public SkipWhileCommand()
        : base("skip-while", "Skips input values while the predicate remains true.", "skip-while { <predicate> }") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var block = context.Arguments.Count == 1 ? context.Arguments[0] as ShellBlock : null;
        var hasPredicateBlock = block is not null;
        var clauses = hasPredicateBlock ? null : WherePredicateMatcher.GetClauses(context);
        var nullablePathCache = new Dictionary<(Type Type, string MemberPath), bool>();
        var skipping = true;

        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            if (skipping)
            {
                skipping = hasPredicateBlock
                    ? await PredicateBlockEvaluator.EvaluateAsync(context, block!, item)
                    : WherePredicateMatcher.MatchesAll(item, clauses!, nullablePathCache, context.Runtime.ObjectAccessor);

                if (skipping)
                {
                    continue;
                }
            }

            yield return item;
        }
    }
}
