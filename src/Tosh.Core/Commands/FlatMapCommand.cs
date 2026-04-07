namespace Tosh.Core.Commands;

[CommandCategory("Pipeline")]
public sealed class FlatMapCommand : ShellCommand
{
    public FlatMapCommand()
        : base("flat-map", "Transforms each pipeline value with a callable or block then flattens the results.", "flat-map <callable|block>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count != 1)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::flat_map_requires_callable_or_block",
                title: "'flat-map' requires exactly one callable value or block.",
                label: "pass a lambda like 'func(x) => ...' or a block like '{ ... }'");
        }

        var operation = FunctionalCommandUtilities.RequireCallableOrBlock(context, 0);

        await foreach (var item in ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken)
                           .WithCancellation(context.CancellationToken))
        {
            var results = await FunctionalCommandUtilities.ExecuteAsync(
                context,
                operation,
                [item],
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["_"] = item,
                });

            foreach (var result in results)
            {
                if (result is string)
                {
                    yield return result;
                }
                else if (result is System.Collections.IEnumerable enumerable)
                {
                    foreach (var inner in enumerable)
                    {
                        yield return inner;
                    }
                }
                else
                {
                    yield return result;
                }
            }
        }
    }
}
