namespace Tosh.Core.Commands;

[CommandCategory("Pipeline")]
public sealed class ReduceCommand : ShellCommand
{
    public ReduceCommand()
        : base("reduce", "Folds the current pipeline into one value using a seed and callable value or block.", "reduce <seed> <callable|block>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count != 2)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::reduce_requires_seed_and_callable",
                title: "'reduce' requires a seed value and a callable value or block.",
                label: "use 'reduce <seed> func(acc, x) => ...' or 'reduce <seed> { ... }'");
        }

        var accumulator = context.Arguments[0];
        var operation = FunctionalCommandUtilities.RequireCallableOrBlock(context, 1);

        await foreach (var item in ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken)
                           .WithCancellation(context.CancellationToken))
        {
            accumulator = await FunctionalCommandUtilities.RequireSingleResultAsync(
                context,
                operation,
                [accumulator, item],
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["acc"] = accumulator,
                    ["_"] = item,
                });
        }

        yield return accumulator;
    }
}
