using Tosh.Runtime;

namespace Tosh.Stdlib.Pipeline;

[Stdlib(StdlibCategory.Pipeline)]
[CommandCategory("Pipeline")]
[CommandArgument("seed", "The initial accumulator value.")]
[CommandArgument("callable|block", "A function that takes (accumulator, current-item) and returns the new accumulator.")]
[CommandExample("echo 1 2 3 4 | scan 0 func(acc, x) => ($acc + $x)", Title = "Running total")]
[CommandExample("echo a b c | scan \"\" { $acc + _ }", Title = "Running string concatenation")]
[CommandOutput("Every intermediate accumulator value (one per input item).")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Folds the pipeline from left to right, yielding each intermediate value.")]
public sealed class ScanCommand : ShellCommand
{
    public ScanCommand()
        : base("scan", "Like reduce but yields every intermediate accumulator value.", "scan <seed> <callable|block>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count != 2)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.scan_requires_seed_and_callable",
                title: "'scan' requires a seed value and a callable value or block.",
                label: "use 'scan <seed> func(acc, x) => ...' or 'scan <seed> { ... }'");
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

            yield return accumulator;
        }
    }
}
