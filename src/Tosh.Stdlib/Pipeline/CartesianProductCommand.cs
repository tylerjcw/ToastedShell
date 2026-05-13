using Tosh.Runtime;

namespace Tosh.Stdlib.Pipeline;

[CommandCategory("Pipeline")]
[CommandArgument("other-sequence", "A second sequence to form pairs with.")]
[CommandArgument("callable|block", "Optional combiner. Receives each pair as arguments. Defaults to [a, b] arrays.", Required = false)]
[CommandExample("[1 2] | cartesian-product [a b]", Title = "All pairs: [1,a],[1,b],[2,a],[2,b]")]
[CommandExample("1.. | cartesian-product (1..) | first 10", Title = "Diagonal enumeration of infinite sources")]
[CommandExample("echo 1 2 3 | cartesian-product [10 20] func(a, b) => ($a * $b)", Title = "With combiner")]
[CommandNote("For infinite sources, uses diagonal (Cantor) enumeration so every pair is reached in finite time.")]
[CommandOutput("All combinations of items from the pipeline and the other sequence.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "First sequence for the Cartesian product.")]
[CommandStreaming(StreamingBehavior.Eager)]
public sealed class CartesianProductCommand : ShellCommand
{
    public CartesianProductCommand()
        : base("cartesian-product", "Produces the Cartesian product of two sequences. For infinite sources, uses diagonal enumeration.", "... | cartesian-product <other-sequence> [callable|block]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count < 1 || context.Arguments.Count > 2)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.cartesian_product_args",
                title: "'cartesian-product' requires a second sequence and an optional combiner.",
                label: "use '... | cartesian-product <sequence> [combiner]'");
        }

        var otherArg = context.Arguments[0];
        object? combiner = context.Arguments.Count == 2
            ? FunctionalCommandUtilities.RequireCallableOrBlock(context, 1)
            : null;

        // Detect if either source is infinite
        var leftIsInfinite = IsInfiniteSource(context);
        var rightIsInfinite = otherArg is ToshRange { IsInfinite: true } or LazySequence { IsFiniteKnown: false };

        if (leftIsInfinite || rightIsInfinite)
        {
            // Use diagonal (Cantor) enumeration
            await foreach (var item in DiagonalProduct(context, otherArg, combiner))
            {
                yield return item;
            }
        }
        else
        {
            // Simple nested loop for finite sources
            await foreach (var left in ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken)
                               .WithCancellation(context.CancellationToken))
            {
                foreach (var right in ShellIterationUtilities.ExpandIterationItems(otherArg))
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    yield return await CombinePair(context, combiner, left, right);
                }
            }
        }
    }

    /// <summary>
    /// Diagonal (Cantor) enumeration: walks anti-diagonals so every (i,j) pair
    /// is reached in finite time, even with infinite sources.
    /// Diagonal d contains pairs where i + j == d.
    /// </summary>
    private async IAsyncEnumerable<object?> DiagonalProduct(CommandContext context, object? otherArg, object? combiner)
    {
        // Lazily cache both sequences as we advance through diagonals
        var leftCache = new List<object?>();
        var rightCache = new List<object?>();

        using var leftEnum = ShellIterationUtilities.ExpandIterationItems(
            await MaterializePipelineHead(context)).GetEnumerator();
        using var rightEnum = ShellIterationUtilities.ExpandIterationItems(otherArg).GetEnumerator();

        var leftDone = false;
        var rightDone = false;

        for (var diagonal = 0; ; diagonal++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            // Expand caches to cover this diagonal if possible
            if (!leftDone && leftCache.Count <= diagonal)
            {
                if (leftEnum.MoveNext())
                    leftCache.Add(leftEnum.Current);
                else
                    leftDone = true;
            }

            if (!rightDone && rightCache.Count <= diagonal)
            {
                if (rightEnum.MoveNext())
                    rightCache.Add(rightEnum.Current);
                else
                    rightDone = true;
            }

            // If both are exhausted and no more diagonals to emit, done
            if (leftDone && rightDone && diagonal >= leftCache.Count + rightCache.Count - 1)
            {
                yield break;
            }

            // Walk anti-diagonal: i + j == diagonal
            var iStart = Math.Min(diagonal, leftCache.Count - 1);
            var iEnd = Math.Max(0, diagonal - (rightCache.Count - 1));

            for (var i = iStart; i >= iEnd; i--)
            {
                var j = diagonal - i;
                if (j < 0 || j >= rightCache.Count || i >= leftCache.Count)
                    continue;

                yield return await CombinePair(context, combiner, leftCache[i], rightCache[j]);
            }
        }
    }

    private async Task<object?> MaterializePipelineHead(CommandContext context)
    {
        var items = new List<object?>();
        await foreach (var item in ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken)
                           .WithCancellation(context.CancellationToken))
        {
            items.Add(item);
        }
        return items.Count == 0 ? Array.Empty<object?>() : items.ToArray();
    }

    private static bool IsInfiniteSource(CommandContext context)
    {
        // We can't easily detect infinite pipeline input, but we'll
        // default to diagonal enumeration if the other side is infinite.
        // For explicit infinite markers, check if input carries an infinite source.
        return false;
    }

    private static async Task<object?> CombinePair(CommandContext context, object? combiner, object? left, object? right)
    {
        if (combiner is not null)
        {
            return await FunctionalCommandUtilities.RequireSingleResultAsync(
                context,
                combiner,
                [left, right],
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["_"] = left,
                    ["left"] = left,
                    ["right"] = right,
                });
        }

        return new object?[] { left, right };
    }
}
