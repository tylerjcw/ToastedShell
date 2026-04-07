namespace Tosh.Core.Commands;

[CommandCategory("Shell")]
public sealed class HistorySearchCommand : ShellCommand
{
    public HistorySearchCommand()
        : base("history-search", "Searches shell history entries by text.", "history-search <text>") { }

    public override IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        return ExecuteCoreAsync(context);
    }

    private static async IAsyncEnumerable<object?> ExecuteCoreAsync(CommandContext context)
    {
        var search = string.Join(" ", context.Arguments.Select(ExternalTextSerializer.Serialize)).Trim();

        if (search.Length == 0)
        {
            var pipedSearch = await TextInputUtilities.ReadScalarValuesFromInputAsync(context, allowEmpty: true);
            search = string.Join(" ", pipedSearch).Trim();
        }

        if (search.Length == 0)
        {
            throw new InvalidOperationException("history-search expects a search string.");
        }

        foreach (var result in context.Runtime.History.Where(entry => entry.Text.Contains(search, StringComparison.OrdinalIgnoreCase)))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            yield return result;
        }
    }
}
