namespace Tosh.Core.Commands;

[CommandCategory("Shell")]
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
