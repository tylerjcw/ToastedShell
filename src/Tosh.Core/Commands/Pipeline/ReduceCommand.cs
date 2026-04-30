namespace Tosh.Core.Commands.Pipeline;

[Stdlib(StdlibCategory.Pipeline)]
[CommandCategory("Pipeline")]
[CommandArgument("seed", "The initial accumulator value.")]
[CommandArgument("callable|block", "A lambda or block that combines the current accumulator with each input item and returns the next accumulator.")]
[CommandExample("echo 1 2 3 4 | reduce 0 func(acc, x) => ($acc + $x)", Title = "Fold numeric values")]
[CommandExample("echo one two three | reduce \"\" { $acc + _.Substring(0, 1) }", Title = "Fold with a block")]
[CommandOutput("Returns the final accumulator value. On an empty input stream, `reduce` returns the seed unchanged.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Consumes the current pipeline from left to right and folds it into one final value.")]
public sealed class ReduceCommand : ShellCommand
{
    public ReduceCommand()
        : base("reduce", "Folds the current pipeline into one value using a seed and callable value or block.", "reduce <seed> <callable|block>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count != 2)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.reduce_requires_seed_and_callable",
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
