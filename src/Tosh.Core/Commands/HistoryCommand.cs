namespace Tosh.Core.Commands;

[CommandCategory("Shell")]
[CommandArgument("search <text>", "Searches history entries by text.", Required = false)]
[CommandArgument("expand <spec>", "Expands a history event specification without running it.", Required = false)]
[CommandArgument("run <spec>", "Resolves and executes a history event specification.", Required = false)]
[CommandArgument("delete <spec>", "Deletes one or more history entries by id or spec.", Required = false)]
[CommandArgument("path|save|reload|clear", "History maintenance subcommands.", Required = false)]
[CommandExample("history", Title = "List history entries")]
[CommandExample("history search build", Title = "Search history")]
[CommandExample("history expand \"!!\"", Title = "Preview a history expansion")]
[CommandExample("history delete 42", Title = "Delete a history entry")]
[CommandNote("History is file-backed in normal interactive sessions and each entry now has a stable id. Use `history path`, `history search <text>`, `history delete <spec>`, `history save`, `history reload`, `history clear`, `history expand 237`, or `history run 237` to inspect or replay it. In the REPL, `Ctrl+R`, `!!`, `!237`, `!-2`, `!prefix`, `!?text?`, `!$`, `!^`, `!*`, and `^old^new^` also work as interactive history features.")]
[CommandOutput("Produces structured history entries, file paths, expanded command text, or replay results depending on the chosen subcommand.")]
[PipelineInput(Description = "History is producer-oriented; replay and expansion are explicit subcommands, while `!` syntax remains REPL-only sugar.")]
public sealed class HistoryCommand : ShellCommand
{
    public HistoryCommand()
        : base("history", "Shows, searches, expands, runs, deletes, saves, reloads, or clears shell history.", "history [status|path|search <text>|expand <spec>|run <spec>|delete <spec>|save|reload|clear]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        await Task.CompletedTask;

        var parsed = ParsedCommandArguments.Parse(context.Arguments);

        if (parsed.Positionals.Count == 0)
        {
            foreach (var entry in context.Runtime.History)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                yield return entry;
            }

            yield break;
        }

        var action = CommandArguments.RequireString(parsed.Positionals, 0, "action");

        switch (action.ToLowerInvariant())
        {
            case "status":
                yield return CreateStatus("status", context);
                yield break;
            case "path":
                yield return context.Runtime.Config.History.FilePath;
                yield break;
            case "expand":
                {
                    var spec = RequireHistorySpec(parsed);
                    yield return HistoryExpansionUtilities.Expand(context.Runtime.History.ToArray(), spec);
                    yield break;
                }
            case "run":
                {
                    var spec = RequireHistorySpec(parsed);
                    var evaluator = context.Runtime.Evaluator
                        ?? throw context.CreateDiagnostic(
                            code: "tosh::history::run_unavailable",
                            title: "History replay is not available in this session.",
                            help: "History replay requires a live evaluator. In normal ToSh sessions, `history run` should be available.");
                    var expanded = HistoryExpansionUtilities.Expand(context.Runtime.History.ToArray(), spec);

                    await foreach (var value in evaluator.EvaluateAsync(expanded, $"history_expand {spec}", context.CancellationToken))
                    {
                        yield return value;
                    }

                    yield break;
                }
            case "search":
                {
                    var search = await ResolveSearchTextAsync(context, parsed);

                    foreach (var result in context.Runtime.History.Where(entry => entry.Text.Contains(search, StringComparison.OrdinalIgnoreCase)))
                    {
                        context.CancellationToken.ThrowIfCancellationRequested();
                        yield return result;
                    }

                    yield break;
                }
            case "delete":
                {
                    var spec = RequireHistorySpec(parsed);
                    var entry = HistoryExpansionUtilities.ResolveEntry(context.Runtime.History.ToArray(), spec);

                    if (!context.Runtime.RemoveHistoryEntry(entry.Id))
                    {
                        throw new InvalidOperationException($"History entry '{entry.Id}' was not found.");
                    }

                    yield return new HistoryDeletionResult(entry.Id, entry.Text);
                    yield break;
                }
            case "save":
                context.Runtime.SaveHistoryToFile();
                yield return CreateStatus("save", context);
                yield break;
            case "reload":
                context.Runtime.ReloadHistoryFromFile();
                yield return CreateStatus("reload", context);
                yield break;
            case "clear":
                context.Runtime.ClearHistory();
                yield return CreateStatus("clear", context);
                yield break;
            default:
                throw new InvalidOperationException("history action must be 'status', 'path', 'search', 'expand', 'run', 'delete', 'save', 'reload', or 'clear'.");
        }
    }

    private static HistoryStatusResult CreateStatus(string action, CommandContext context)
    {
        return new HistoryStatusResult(
            Action: action,
            FilePath: context.Runtime.Config.History.FilePath,
            EntryCount: context.Runtime.History.Count,
            Persistent: context.Runtime.Config.History.Persistent,
            Deduplication: context.Runtime.Config.History.Deduplication);
    }

    private static string RequireHistorySpec(ParsedCommandArguments parsed)
    {
        if (parsed.Positionals.Count < 2)
        {
            throw new InvalidOperationException("history expand/run/delete expects a history designator like `237`, `-2`, `?text?`, or the quoted forms `\"!!\"`, `\"!237\"`, and `\"!$\"`.");
        }

        return parsed.Positionals[1] switch
        {
            string text => text,
            sbyte or byte or short or ushort or int or uint or long or ulong
                => parsed.Positionals[1]!.ToString()!,
            _ => throw new InvalidOperationException("Argument 'spec' must be a history id, relative offset, or string designator."),
        };
    }

    private static async Task<string> ResolveSearchTextAsync(CommandContext context, ParsedCommandArguments parsed)
    {
        if (parsed.Positionals.Count > 1)
        {
            return string.Join(" ", parsed.Positionals.Skip(1).Select(ExternalTextSerializer.Serialize)).Trim();
        }

        var pipedSearch = await TextInputUtilities.ReadScalarValuesFromInputAsync(context, allowEmpty: true);
        var search = string.Join(" ", pipedSearch).Trim();

        if (search.Length == 0)
        {
            throw new InvalidOperationException("history search expects a search string.");
        }

        return search;
    }
}

public sealed record HistoryStatusResult(
    string Action,
    string FilePath,
    int EntryCount,
    bool Persistent,
    ToshHistoryDeduplicationMode Deduplication);

public sealed record HistoryDeletionResult(long Id, string Text);
