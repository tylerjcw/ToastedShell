namespace Tosh.Core.Commands.Pipeline;

[Stdlib(StdlibCategory.Pipeline)]
[CommandCategory("Pipeline")]
[CommandArgument("count", "The number of objects to return from the end. Defaults to 1.", Required = false, Kind = "expression")]
[CommandExample("echo 1 2 3 | last", Title = "Get the last item")]
[CommandExample("ls | last 3", Title = "Get the last three items")]
[CommandOutput("The last N pipeline objects.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Buffers the pipeline and returns the final N items.")]
public sealed class LastCommand : ShellCommand
{
    public LastCommand()
        : base("last", "Returns the last object or last N objects from the pipeline.", "last [count]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var count = GetCount(context);

        if (count == 0)
        {
            yield break;
        }

        var buffer = new Queue<object?>(count);

        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            if (buffer.Count == count)
            {
                buffer.Dequeue();
            }

            buffer.Enqueue(item);
        }

        foreach (var item in buffer)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
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
            throw new InvalidOperationException("The 'last' command accepts at most one count argument.");
        }

        var count = CommandArguments.RequireConverted<int>(context.Arguments, 0, "count");

        if (count < 0)
        {
            throw new InvalidOperationException("The 'last' command requires a non-negative count.");
        }

        return count;
    }
}
