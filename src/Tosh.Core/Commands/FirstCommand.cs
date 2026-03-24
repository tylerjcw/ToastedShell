namespace Tosh.Core.Commands;

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

        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
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
