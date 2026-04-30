namespace Tosh.Core.Commands.Pipeline;

[Stdlib(StdlibCategory.Pipeline)]
[CommandCategory("Pipeline")]
[CommandArgument("k", "The number of elements in each combination.")]
[CommandExample("[1 2 3] | combinations 2", Title = "All 2-element subsets")]
[CommandExample("echo a b c d | combinations 3", Title = "All 3-element subsets")]
[CommandOutput("Arrays of k elements from the pipeline in lexicographic order.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Items to select combinations from.")]
public sealed class CombinationsCommand : ShellCommand
{
    public CombinationsCommand()
        : base("combinations", "Yields all k-element combinations (subsets) of the pipeline items.", "... | combinations <k>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count != 1)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.combinations_requires_k",
                title: "'combinations' requires exactly one integer argument (k).",
                label: "use '... | combinations <k>'");
        }

        var k = Convert.ToInt32(context.Arguments[0]);
        if (k < 0)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.combinations_k_non_negative",
                title: "k must be non-negative.",
                argumentIndex: 0,
                label: "must be >= 0");
        }

        // Materialize input
        var items = new List<object?>();
        await foreach (var item in ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken)
                           .WithCancellation(context.CancellationToken))
        {
            items.Add(item);
        }

        if (k > items.Count)
        {
            yield break;
        }

        if (k == 0)
        {
            yield return Array.Empty<object?>();
            yield break;
        }

        // Generate combinations using iterative index tracking
        var indices = new int[k];
        for (var i = 0; i < k; i++)
        {
            indices[i] = i;
        }

        while (true)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var combo = new object?[k];
            for (var i = 0; i < k; i++)
            {
                combo[i] = items[indices[i]];
            }

            yield return combo;

            // Advance to next combination
            var pos = k - 1;
            while (pos >= 0 && indices[pos] == items.Count - k + pos)
            {
                pos--;
            }

            if (pos < 0)
            {
                yield break;
            }

            indices[pos]++;
            for (var i = pos + 1; i < k; i++)
            {
                indices[i] = indices[i - 1] + 1;
            }
        }
    }
}
