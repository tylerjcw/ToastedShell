using Tosh.Runtime;

namespace Tosh.Stdlib.Shell;

[ShellOnly]
[CommandCategory("Shell")]
[CommandArgument("text", "Case-insensitive text to search for. May also be supplied from the pipeline.")]
[CommandExample("history-search git", Title = "Search history by argument")]
[CommandExample("echo build | history-search", Title = "Search history from the pipeline")]
[CommandOutput("History records matching the query: index, command line, and timestamp.")]
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

        foreach (var result in context.Shell().History.Where(entry => entry.Text.Contains(search, StringComparison.OrdinalIgnoreCase)))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            yield return result;
        }
    }
}
