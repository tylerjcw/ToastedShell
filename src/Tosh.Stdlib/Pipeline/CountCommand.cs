using Tosh.Runtime;

namespace Tosh.Stdlib.Pipeline;

[Stdlib(StdlibCategory.Pipeline)]
[CommandCategory("Pipeline")]
[CommandExample("echo a b c | count", Title = "Count items in a pipeline")]
[CommandExample("ls | count", Title = "Count files in the current directory")]
[CommandOutput("An integer representing the total number of pipeline objects.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Consumes the entire pipeline and returns the count.")]
public sealed class CountCommand : ShellCommand
{
    public CountCommand()
        : base("count", "Counts the number of objects in the current pipeline.", "count") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count != 0)
        {
            throw new InvalidOperationException("The 'count' command does not accept arguments.");
        }

        var count = 0;

        var input = ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken);

        await foreach (var _ in input.WithCancellation(context.CancellationToken))
        {
            count++;
        }

        yield return count;
    }
}
