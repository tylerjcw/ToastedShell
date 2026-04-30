using Tosh.Runtime;
using Tosh.Runtime.Generated;
using Tosh.Tui.Requests;

namespace Tosh.Stdlib.Shell;

[Stdlib(StdlibCategory.Shell)]
[CommandCategory("Shell")]
[CommandArgument("topic", "The command, language topic, type, or external executable to describe.", Required = false)]
[CommandArgument("--cli [query]", "Opens the inline fuzzy tree browser, optionally seeded with an initial query or topic.", Required = false)]
[CommandArgument("browse [query]", "Opens the full-screen help browser, optionally filtered by an initial query.", Required = false)]
[CommandArgument("search <query>", "Searches help topics by name, alias, category, and description.", Required = false)]
[CommandArgument("related <topic>", "Shows related topics for the given command or language feature.", Required = false)]
[CommandArgument("categories", "Lists help categories and topic counts.", Required = false)]
[CommandOption("--cli", "Open the inline fuzzy help browser instead of returning help objects.")]
[CommandExample("help ls", Title = "Describe a command")]
[CommandExample("help search regex", Title = "Search help")]
[CommandExample("help --cli regex", Title = "Open the inline fuzzy help browser")]
[CommandExample("help browse grep", Title = "Open the help browser")]
[CommandNote("Use `help search <query>` or `apropos <query>` to find commands and language topics quickly, `help --cli` for the inline fuzzy tree browser, or `help browse` for the fullscreen split-pane browser. In the REPL, `F1` opens the inline help browser seeded from the token under the cursor, and `Alt+H` is available as a fallback on terminals that do not expose function keys cleanly.")]
[CommandOutput("Produces help summaries, full help topics, search results, category rows, launches the inline browser with `--cli`, or returns an interactive browser request for `help browse` depending on the form.")]
[PipelineInput(AcceptsScalar = true, Description = "With no explicit args, piped scalar values are treated as help topics. `search` and `related` also consume piped query/topic text.")]
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
            // Polymorphic fallback: `help tosh.parser.foo` looks the code up in the
            // generated diagnostic-code manifest and returns it as a help topic.
            if (first.StartsWith("tosh.", StringComparison.OrdinalIgnoreCase) &&
                BuildDiagnosticCodeTopic(first) is { } codeTopic)
            {
                yield return codeTopic;
                yield break;
            }

            throw new InvalidOperationException($"Help topic '{first}' was not found. Try '{Name} search {first}'.");
        }

        yield return resolvedTopic;
    }

    private static HelpTopic? BuildDiagnosticCodeTopic(string code)
    {
        var info = DiagnosticCodeManifest.TryGet(code);
        if (info is null)
        {
            return null;
        }

        var description = string.IsNullOrWhiteSpace(info.Title)
            ? $"Diagnostic code in the `tosh.{info.Namespace}` namespace."
            : info.Title;

        var notes = $"First emitted at `{info.SourceFile}:{info.SourceLine}`. " +
                    $"Suppress with `hush {info.Code}` (scope-local) or by adding it to " +
                    "`$tosh.Config.Diagnostics.Hushed` from `profile.tosh`. Errors are never suppressible.";

        return new HelpTopic(
            Name: info.Code,
            Kind: HelpSubjectKind.DiagnosticCode,
            Category: $"Diagnostics — tosh.{info.Namespace}",
            Description: description,
            Usage: info.Code,
            Aliases: Array.Empty<string>(),
            Related: Array.Empty<string>(),
            Examples: Array.Empty<string>(),
            Path: null,
            Notes: notes,
            Arguments: null,
            Options: null,
            PipelineInput: null,
            Output: info.Help,
            ExampleItems: null);
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
                code: "tosh.help.no_inline_provider",
                title: "Inline help (--cli) is not available in this environment.",
                help: "The --cli flag requires an interactive terminal. Remove --cli to use the fullscreen help browser or normal help output.");
    }
}
