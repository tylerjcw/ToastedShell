namespace Tosh.Core.Commands;

[CommandCategory("Pipeline")]
[CommandArgument("k", "The number of elements in each permutation. Defaults to the full length if omitted.", Required = false)]
[CommandExample("[1 2 3] | permutations", Title = "All 6 permutations of 3 elements")]
[CommandExample("echo a b c | permutations 2", Title = "All 2-element orderings")]
[CommandOutput("Arrays of k elements representing each permutation.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Items to permute.")]
public sealed class PermutationsCommand : ShellCommand
{
    public PermutationsCommand()
        : base("permutations", "Yields all k-element permutations (orderings) of the pipeline items. Defaults to full-length permutations.", "... | permutations [k]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count > 1)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::permutations_args",
                title: "'permutations' accepts at most one integer argument (k).",
                label: "use '... | permutations [k]'");
        }

        // Materialize input
        var items = new List<object?>();
        await foreach (var item in ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken)
                           .WithCancellation(context.CancellationToken))
        {
            items.Add(item);
        }

        var n = items.Count;
        var k = context.Arguments.Count == 1 ? Convert.ToInt32(context.Arguments[0]) : n;

        if (k < 0)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::permutations_k_non_negative",
                title: "k must be non-negative.",
                argumentIndex: 0,
                label: "must be >= 0");
        }

        if (k > n)
        {
            yield break;
        }

        if (k == 0)
        {
            yield return Array.Empty<object?>();
            yield break;
        }

        // Generate k-permutations using index arrays
        // Uses the "factoradic" approach: track which indices are selected and in what order
        var indices = new int[n];
        for (var i = 0; i < n; i++) indices[i] = i;

        var cycles = new int[k];
        for (var i = 0; i < k; i++) cycles[i] = n - i;

        // Yield first permutation
        var first = new object?[k];
        for (var i = 0; i < k; i++) first[i] = items[indices[i]];
        yield return first;

        // Generate subsequent permutations
        while (true)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var found = false;
            for (var i = k - 1; i >= 0; i--)
            {
                cycles[i]--;
                if (cycles[i] == 0)
                {
                    // Rotate indices[i:] left by 1
                    var temp = indices[i];
                    for (var j = i; j < n - 1; j++) indices[j] = indices[j + 1];
                    indices[n - 1] = temp;
                    cycles[i] = n - i;
                }
                else
                {
                    // Swap indices[i] and indices[n - cycles[i]]
                    var swapPos = n - cycles[i];
                    (indices[i], indices[swapPos]) = (indices[swapPos], indices[i]);

                    var perm = new object?[k];
                    for (var j = 0; j < k; j++) perm[j] = items[indices[j]];
                    yield return perm;

                    found = true;
                    break;
                }
            }

            if (!found)
            {
                yield break;
            }
        }
    }
}
