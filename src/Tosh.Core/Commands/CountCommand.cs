namespace Tosh.Core.Commands;

[CommandCategory("Pipeline")]
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
