using Tosh.Runtime;

namespace Tosh.Stdlib.Pipeline;

[CommandCategory("Pipeline")]
[CommandArgument("count", "The number of objects to skip. Defaults to 1.", Required = false)]
[CommandExample("echo 1 2 3 4 5 | skip 2", Title = "Skip the first two items")]
[CommandExample("echo a b c | skip", Title = "Skip the first item")]
[CommandOutput("The remaining pipeline objects after skipping the first N.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Consumes and discards the first N items, then yields the rest.")]
public sealed class SkipCommand : ShellCommand
{
    public SkipCommand()
        : base("skip", "Skips the first object or first N objects from the pipeline.", "skip [count]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var count = GetCount(context);
        var skipped = 0;

        await foreach (var item in ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken)
                           .WithCancellation(context.CancellationToken))
        {
            if (skipped < count)
            {
                skipped++;
                continue;
            }

            yield return item;
        }
    }

    private static int GetCount(CommandContext context)
    {
        if (context.Arguments.Count == 0)
        {
            return 1;
        }

        if (context.Arguments.Count > 1)
        {
            throw new InvalidOperationException("The 'skip' command accepts at most one count argument.");
        }

        var count = CommandArguments.RequireConverted<int>(context.Arguments, 0, "count");

        if (count < 0)
        {
            throw new InvalidOperationException("The 'skip' command requires a non-negative count.");
        }

        return count;
    }
}
