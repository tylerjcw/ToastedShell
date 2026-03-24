namespace Tosh.Core.Commands;

public sealed class HelpCommand : ShellCommand
{
    public HelpCommand(string name = "help")
        : base(name, "Shows searchable Tosh help for commands, language topics, CLR types, and externals.", $"{name} [topic | search <query> | related <topic> | categories]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count == 0)
        {
            foreach (var topic in HelpCatalog.BuildSummaries(context.Runtime))
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                yield return topic;
            }

            yield break;
        }

        var first = CommandArguments.RequireString(context.Arguments, 0, "topic");

        if (string.Equals(first, "search", StringComparison.OrdinalIgnoreCase))
        {
            var query = string.Join(" ", context.Arguments.Skip(1).Select(argument => argument?.ToString() ?? string.Empty)).Trim();

            if (query.Length == 0)
            {
                throw new InvalidOperationException($"The '{Name} search' form requires a query.");
            }

            foreach (var result in HelpCatalog.Search(context.Runtime, query))
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                yield return result;
            }

            yield break;
        }

        if (string.Equals(first, "related", StringComparison.OrdinalIgnoreCase))
        {
            if (context.Arguments.Count < 2)
            {
                throw new InvalidOperationException($"The '{Name} related' form requires a topic name.");
            }

            var relatedTopic = CommandArguments.RequireString(context.Arguments, 1, "topic");

            foreach (var result in HelpCatalog.GetRelated(context.Runtime, relatedTopic))
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                yield return result;
            }

            yield break;
        }

        if (string.Equals(first, "categories", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var category in HelpCatalog.BuildCategories(context.Runtime))
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                yield return category;
            }

            yield break;
        }

        var resolvedTopic = HelpCatalog.ResolveTopic(context.Runtime, first);

        if (resolvedTopic is null)
        {
            throw new InvalidOperationException($"Help topic '{first}' was not found. Try '{Name} search {first}'.");
        }

        yield return resolvedTopic;
    }
}
