namespace Tosh.Core.Commands;

[CommandCategory("Pipeline")]
public sealed class PartitionCommand : ShellCommand
{
    public PartitionCommand()
        : base("partition", "Splits pipeline values into two lists based on a predicate: [matches, non-matches].", "partition <callable|block>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count != 1)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::partition_requires_callable_or_block",
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
