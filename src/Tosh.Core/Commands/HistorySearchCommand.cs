namespace Tosh.Core.Commands;

public sealed class HistorySearchCommand : ShellCommand
{
    public HistorySearchCommand()
        : base("history-search", "Searches shell history entries by text.", "history-search <text>") { }

    public override IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count != 1)
        {
            throw new InvalidOperationException("history-search expects exactly one search string.");
        }

        var search = CommandArguments.RequireString(context.Arguments, 0, "search text");
        var results = context.Runtime.History
            .Where(entry => entry.Text.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Cast<object?>()
            .ToArray();
        return AsyncEnumerableExtensions.FromEnumerable(results);
    }
}
