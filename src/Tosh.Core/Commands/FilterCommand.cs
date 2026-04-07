namespace Tosh.Core.Commands;

[CommandCategory("Pipeline")]
public sealed class FilterCommand : ShellCommand
{
    public FilterCommand()
        : base("filter", "Filters pipeline values with a callable value or block predicate.", "filter <callable|block>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count != 1)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::filter_requires_callable_or_block",
                title: "'filter' requires exactly one callable value or block.",
                label: "pass a lambda like 'func(x) => ...' or a block like '{ ... }'");
        }

        var operation = FunctionalCommandUtilities.RequireCallableOrBlock(context, 0);
        var (tree, items) = await ShellIterationUtilities.PeekForTreeAsync(context.Input, context.CancellationToken);

        if (tree is not null)
        {
            var pruned = await PruneTreeAsync(context, operation, tree);

            if (pruned is not null)
            {
                yield return pruned;
            }

            yield break;
        }

        await foreach (var item in items.WithCancellation(context.CancellationToken))
        {
            if (await EvaluatePredicateAsync(context, operation, item))
            {
                yield return item;
            }
        }
    }

    private static async Task<bool> EvaluatePredicateAsync(CommandContext context, object operation, object? item)
    {
        return await FunctionalCommandUtilities.EvaluatePredicateAsync(
            context,
            operation,
            [item],
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["_"] = item,
            });
    }

    private static async Task<TreeEntryInfo?> PruneTreeAsync(
        CommandContext context,
        object operation,
        TreeEntryInfo node)
    {
        var selfMatches = await EvaluatePredicateAsync(context, operation, node);

        if (node.Children.Count == 0)
        {
            return selfMatches ? node : null;
        }

        var prunedChildren = new List<TreeEntryInfo>();

        foreach (var child in node.Children)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var pruned = await PruneTreeAsync(context, operation, child);

            if (pruned is not null)
            {
                prunedChildren.Add(pruned);
            }
        }

        if (selfMatches || prunedChildren.Count > 0)
        {
            return node with { Children = prunedChildren.ToArray() };
        }

        return null;
    }
}
