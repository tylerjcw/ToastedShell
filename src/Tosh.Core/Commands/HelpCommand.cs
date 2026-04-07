namespace Tosh.Core.Commands;

[CommandCategory("Shell")]
public sealed class HelpCommand : ShellCommand
{
    public HelpCommand(string name = "help")
        : base(name, "Shows searchable Tosh help for commands, language topics, CLR types, and externals.", $"{name} [--cli] [topic ... | browse [query] | search <query> | related <topic> | categories]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var inlineCli = false;
        var arguments = new List<object?>(context.Arguments.Count);

        foreach (var argument in context.Arguments)
        {
            if (argument is string text && string.Equals(text, "--cli", StringComparison.OrdinalIgnoreCase))
            {
                inlineCli = true;
                continue;
            }

            arguments.Add(argument);
        }

        if (inlineCli)
        {
            var provider = RequireInlineProvider(context);
            var (initialQuery, initialTopicName) = await ResolveInlineBrowseSeedAsync(context, arguments);
            provider.BrowseHelp(initialQuery, initialTopicName);
            yield break;
        }

        if (arguments.Count == 0)
        {
            var pipedTopics = await TextInputUtilities.ReadScalarValuesFromInputAsync(context, allowEmpty: true);

            if (pipedTopics.Count > 0)
            {
                foreach (var pipedTopic in pipedTopics)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    var topic = pipedTopic.Trim();

                    if (topic.Length == 0)
                    {
                        continue;
                    }

                    var pipedResolvedTopic = HelpCatalog.ResolveTopic(context.Runtime, topic);

                    if (pipedResolvedTopic is null)
                    {
                        throw new InvalidOperationException($"Help topic '{topic}' was not found. Try '{Name} search {topic}'.");
                    }

                    yield return pipedResolvedTopic;
                }

                yield break;
            }

            foreach (var topic in HelpCatalog.BuildSummaries(context.Runtime))
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                yield return topic;
            }

            yield break;
        }

        var first = CommandArguments.RequireString(arguments, 0, "topic");

        if (string.Equals(first, "search", StringComparison.OrdinalIgnoreCase))
        {
            var query = string.Join(" ", arguments.Skip(1).Select(argument => argument?.ToString() ?? string.Empty)).Trim();

            if (query.Length == 0)
            {
                var pipedQuery = await TextInputUtilities.ReadScalarValuesFromInputAsync(context, allowEmpty: true);
                query = string.Join(" ", pipedQuery).Trim();
            }

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

        if (string.Equals(first, "browse", StringComparison.OrdinalIgnoreCase))
        {
            var initialQuery = arguments.Count > 1
                ? string.Join(" ", arguments.Skip(1).Select(argument => argument?.ToString() ?? string.Empty)).Trim()
                : null;
            yield return new HelpBrowseRequest(string.IsNullOrWhiteSpace(initialQuery) ? null : initialQuery, null);
            yield break;
        }

        if (string.Equals(first, "related", StringComparison.OrdinalIgnoreCase))
        {
            if (arguments.Count >= 2)
            {
                var relatedTopic = CommandArguments.RequireString(arguments, 1, "topic");

                foreach (var result in HelpCatalog.GetRelated(context.Runtime, relatedTopic))
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    yield return result;
                }

                yield break;
            }

            var pipedTopics = await TextInputUtilities.ReadScalarValuesFromInputAsync(context, allowEmpty: true);

            if (pipedTopics.Count == 0)
            {
                throw new InvalidOperationException($"The '{Name} related' form requires a topic name.");
            }

            foreach (var topic in pipedTopics)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var relatedTopic = topic.Trim();

                if (relatedTopic.Length == 0)
                {
                    continue;
                }

                foreach (var result in HelpCatalog.GetRelated(context.Runtime, relatedTopic))
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    yield return result;
                }
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

    private static async Task<(string? InitialQuery, string? InitialTopicName)> ResolveInlineBrowseSeedAsync(
        CommandContext context,
        IReadOnlyList<object?> arguments)
    {
        string? initialQuery = null;

        if (arguments.Count == 0)
        {
            initialQuery = await ReadInlinePipedQueryAsync(context);
        }
        else
        {
            var first = CommandArguments.RequireString(arguments, 0, "topic");

            if (string.Equals(first, "browse", StringComparison.OrdinalIgnoreCase))
            {
                initialQuery = arguments.Count > 1
                    ? string.Join(" ", arguments.Skip(1).Select(argument => argument?.ToString() ?? string.Empty)).Trim()
                    : await ReadInlinePipedQueryAsync(context);
            }
            else if (string.Equals(first, "search", StringComparison.OrdinalIgnoreCase))
            {
                initialQuery = arguments.Count > 1
                    ? string.Join(" ", arguments.Skip(1).Select(argument => argument?.ToString() ?? string.Empty)).Trim()
                    : await ReadInlinePipedQueryAsync(context);
            }
            else if (string.Equals(first, "related", StringComparison.OrdinalIgnoreCase))
            {
                initialQuery = arguments.Count > 1
                    ? CommandArguments.RequireString(arguments, 1, "topic")
                    : await ReadInlinePipedQueryAsync(context);
            }
            else if (string.Equals(first, "categories", StringComparison.OrdinalIgnoreCase))
            {
                initialQuery = null;
            }
            else
            {
                initialQuery = string.Join(" ", arguments.Select(argument => argument?.ToString() ?? string.Empty)).Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(initialQuery))
        {
            return (null, null);
        }

        var initialTopicName = HelpCatalog.ResolveTopic(context.Runtime, initialQuery)?.Name;
        return (initialQuery, initialTopicName);
    }

    private static async Task<string?> ReadInlinePipedQueryAsync(CommandContext context)
    {
        var pipedTopics = await TextInputUtilities.ReadScalarValuesFromInputAsync(context, allowEmpty: true);
        var query = string.Join(" ", pipedTopics.Where(topic => !string.IsNullOrWhiteSpace(topic))).Trim();
        return query.Length == 0 ? null : query;
    }

    private static IInlinePromptProvider RequireInlineProvider(CommandContext context)
    {
        return context.Runtime.InlinePrompts
            ?? throw context.CreateDiagnostic(
                code: "tosh::help::no_inline_provider",
                title: "Inline help (--cli) is not available in this environment.",
                help: "The --cli flag requires an interactive terminal. Remove --cli to use the fullscreen help browser or normal help output.");
    }
}
