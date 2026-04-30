namespace Tosh.Core.Commands.Shell;

[Stdlib(StdlibCategory.Shell)]
[CommandCategory("Shell")]
[CommandArgument("query", "The search term to match against help topics.")]
[CommandExample("apropos json", Title = "Search help for JSON-related topics")]
[CommandExample("apropos loop", Title = "Search help for loop constructs")]
[CommandNote("Apropos performs fuzzy help search across commands and Tosh language topics.")]
[CommandOutput("Matching help topic summaries with relevance scores.")]
[PipelineInput(AcceptsScalar = true, Description = "Reads the query from the pipeline if not given as an argument.")]
public sealed class AproposCommand : ShellCommand
{
    public AproposCommand()
        : base("apropos", "Searches Tosh help topics with fuzzy matching.", "apropos <query>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var query = string.Join(" ", context.Arguments.Select(argument => argument?.ToString() ?? string.Empty)).Trim();

        if (query.Length == 0)
        {
            var pipedQuery = await TextInputUtilities.ReadScalarValuesFromInputAsync(context, allowEmpty: true);
            query = string.Join(" ", pipedQuery).Trim();
        }

        if (query.Length == 0)
        {
            throw new InvalidOperationException("The 'apropos' command requires a search query.");
        }

        foreach (var result in HelpCatalog.Search(context.Runtime, query))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            yield return result;
        }
    }
}
