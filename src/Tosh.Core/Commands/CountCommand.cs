namespace Tosh.Core.Commands;

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

        await foreach (var _ in context.Input.WithCancellation(context.CancellationToken))
        {
            count++;
        }

        yield return count;
    }
}
