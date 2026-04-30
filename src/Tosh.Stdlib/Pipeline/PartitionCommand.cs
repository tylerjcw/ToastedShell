using Tosh.Runtime;

namespace Tosh.Stdlib.Pipeline;

[CommandCategory("Pipeline")]
[CommandArgument("callable|block", "A predicate that returns true or false for each item.")]
[CommandExample("echo 1 2 3 4 5 | partition { _ > 3 }", Title = "Partition by a condition")]
[CommandExample("ls | partition { _.Extension == \".cs\" }", Title = "Separate C# files from others")]
[CommandOutput("A two-element array: [items-where-true, items-where-false].")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Consumes the pipeline and splits items into two groups by predicate.")]
public sealed class PartitionCommand : ShellCommand
{
    public PartitionCommand()
        : base("partition", "Splits pipeline values into two lists based on a predicate: [matches, non-matches].", "partition <callable|block>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count != 1)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.partition_requires_callable_or_block",
                title: "'partition' requires exactly one callable value or block.",
                label: "pass a lambda like 'func(x) => ...' or a block like '{ ... }'");
        }

        var operation = FunctionalCommandUtilities.RequireCallableOrBlock(context, 0);
        var matches = new List<object?>();
        var nonMatches = new List<object?>();

        await foreach (var item in ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken)
                           .WithCancellation(context.CancellationToken))
        {
            var result = await FunctionalCommandUtilities.EvaluatePredicateAsync(
                context,
                operation,
                [item],
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["_"] = item,
                });

            if (result)
            {
                matches.Add(item);
            }
            else
            {
                nonMatches.Add(item);
            }
        }

        yield return new object?[] { matches.ToArray(), nonMatches.ToArray() };
    }
}
