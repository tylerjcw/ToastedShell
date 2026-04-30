using Tosh.Runtime;

namespace Tosh.Stdlib.Pipeline;

[Stdlib(StdlibCategory.Pipeline)]
[CommandCategory("Pipeline")]
[CommandArgument("count", "The number of objects to return. Defaults to 1.", Required = false, Kind = "expression")]
[CommandExample("echo 1 2 3 | first", Title = "Get the first item")]
[CommandExample("ls | first 5", Title = "Get the first five items")]
[CommandOutput("The first N pipeline objects.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Yields the first N items then stops consuming.")]
public sealed class FirstCommand : ShellCommand
{
    public FirstCommand()
        : base("first", "Returns the first object or first N objects from the pipeline.", "first [count]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var count = GetCount(context);

        if (count == 0)
        {
            yield break;
        }

        var emitted = 0;

        var input = ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken);

        await foreach (var item in input.WithCancellation(context.CancellationToken))
        {
            yield return item;
            emitted++;

            if (emitted >= count)
            {
                yield break;
            }
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
            throw new InvalidOperationException("The 'first' command accepts at most one count argument.");
        }

        var count = CommandArguments.RequireConverted<int>(context.Arguments, 0, "count");

        if (count < 0)
        {
            throw new InvalidOperationException("The 'first' command requires a non-negative count.");
        }

        return count;
    }
}
