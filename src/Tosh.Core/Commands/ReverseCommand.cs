namespace Tosh.Core.Commands;

public sealed class ReverseCommand : ShellCommand
{
    public ReverseCommand()
        : base("reverse", "Reverses the order of the current pipeline objects.", "reverse") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count != 0)
        {
            throw new InvalidOperationException("The 'reverse' command does not accept arguments.");
        }

        var items = new List<object?>();

        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            items.Add(item);
        }

        for (var index = items.Count - 1; index >= 0; index--)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            yield return items[index];
        }
    }
}
