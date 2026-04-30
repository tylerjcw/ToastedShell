using Tosh.Runtime;

namespace Tosh.Stdlib.Pipeline;

[Stdlib(StdlibCategory.Pipeline)]
[CommandCategory("Pipeline")]
[CommandArgument("other-sequence", "An array or list to merge pairwise with the pipeline.")]
[CommandArgument("callable|block", "Optional combiner. Receives each pair as arguments. Defaults to creating two-element arrays.", Required = false)]
[CommandExample("echo a b c | zip [1 2 3]", Title = "Pair pipeline with an array")]
[CommandExample("echo 1 2 3 | zip [10 20 30] func(a, b) => ($a + $b)", Title = "Zip with a combiner")]
[CommandOutput("One result per pair. Without a combiner, yields two-element arrays.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Merges the pipeline with another sequence pairwise.")]
public sealed class ZipCommand : ShellCommand
{
    public ZipCommand()
        : base("zip", "Merges two sequences pairwise. The second sequence is the first argument (an array).", "zip <other-sequence> [callable|block]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count < 1 || context.Arguments.Count > 2)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.zip_requires_sequence",
                title: "'zip' requires a second sequence and an optional combiner block.",
                label: "use 'zip <array> [block]'");
        }

        var otherItems = ResolveSequence(context, context.Arguments[0]);
        object? combiner = context.Arguments.Count == 2
            ? FunctionalCommandUtilities.RequireCallableOrBlock(context, 1)
            : null;

        var otherIndex = 0;
        await foreach (var item in ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken)
                           .WithCancellation(context.CancellationToken))
        {
            if (otherIndex >= otherItems.Count)
            {
                yield break;
            }

            var otherItem = otherItems[otherIndex++];

            if (combiner is not null)
            {
                var combined = await FunctionalCommandUtilities.RequireSingleResultAsync(
                    context,
                    combiner,
                    [item, otherItem],
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["_"] = item,
                        ["left"] = item,
                        ["other"] = otherItem,
                        ["right"] = otherItem,
                        ["acc"] = otherItem,
                    });

                yield return combined;
            }
            else
            {
                yield return new object?[] { item, otherItem };
            }
        }
    }

    private static IReadOnlyList<object?> ResolveSequence(CommandContext context, object? argument)
    {
        return argument switch
        {
            object?[] array => array,
            IReadOnlyList<object?> list => list,
            string => throw context.CreateDiagnostic(
                code: "tosh.runtime.zip_requires_sequence",
                title: "'zip' requires an array or list as the second sequence.",
                argumentIndex: 0,
                label: "a string is not a valid sequence for zip"),
            System.Collections.IEnumerable enumerable => enumerable.Cast<object?>().ToArray(),
            _ => throw context.CreateDiagnostic(
                code: "tosh.runtime.zip_requires_sequence",
                title: "'zip' requires an array or list as the second sequence.",
                argumentIndex: 0,
                label: "this value is not a sequence"),
        };
    }
}
