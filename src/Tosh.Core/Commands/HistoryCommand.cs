namespace Tosh.Core.Commands;

public sealed class HistoryCommand : ShellCommand
{
    public HistoryCommand()
        : base("history", "Shows command history for the current Tosh session.", "history") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        foreach (var entry in context.Runtime.History)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            yield return entry;
        }
    }
}
