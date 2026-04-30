namespace Tosh.Core.Commands.Pipeline;

[Stdlib(StdlibCategory.Pipeline)]
[CommandCategory("Pipeline")]
[CommandArgument("predicate", "A predicate block or callable that returns a boolean.", TypeName = "block|callable", Kind = "block")]
[CommandExample("ls -la | where _.Type == file", Title = "Filter by property")]
[CommandExample("ls -la | where func(item) => ($item.Name.ToLower().EndsWith(\".md\"))", Title = "Lambda predicate")]
[CommandNote("Inside predicate expressions, bare member access resolves against the current pipeline object.")]
[CommandOutput("Pipeline objects for which the predicate returned true.")]
[PipelineInput(AcceptsScalar = true, Description = "Consumes any pipeline objects and tests each against the predicate.")]
public sealed class WhereCommand : ShellCommand
{
    public WhereCommand()
        : base("where", "Filters pipeline objects with a predicate block or callable.", "where <predicate-expression|callable>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count != 1)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.predicate_expression_required",
                title: "'where' requires a predicate expression.",
                label: "write a predicate block like '{ ... }' or pass a callable value",
                help: "predicate commands now use one expression mode everywhere.");
        }

        var predicate = await FunctionalCommandUtilities.ResolveCallableOrBlockAsync(
            context,
            FunctionalCommandUtilities.RequireCallableOrBlock(context, 0));
        var (tree, items) = await ShellIterationUtilities.PeekForTreeAsync(context.Input, context.CancellationToken);

        if (tree is not null)
        {
            var pruned = await PruneTreeAsync(context, predicate, tree);

            if (pruned is not null)
            {
                yield return pruned;
            }

            yield break;
        }

        await foreach (var item in items.WithCancellation(context.CancellationToken))
        {
            if (await FunctionalCommandUtilities.EvaluatePredicateAsync(
                    context,
                    predicate,
                    [item],
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["_"] = item,
                    }))
            {
                yield return item;
            }
        }
    }

    private static async Task<TreeEntryInfo?> PruneTreeAsync(
        CommandContext context,
        object predicate,
        TreeEntryInfo node)
    {
        var selfMatches = await FunctionalCommandUtilities.EvaluatePredicateAsync(
            context,
            predicate,
            [node],
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["_"] = node,
            });

        if (node.Children.Count == 0)
        {
            return selfMatches ? node : null;
        }

        var prunedChildren = new List<TreeEntryInfo>();

        foreach (var child in node.Children)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var pruned = await PruneTreeAsync(context, predicate, child);

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
