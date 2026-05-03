using Tosh.Tui.Requests;

using Tosh.Runtime;

namespace Tosh.Stdlib.Sys;

[CommandCategory("System")]
[CommandArgument("filter", "Optional case-insensitive name filter.", Required = false)]
[CommandArgument("browse [filter]", "Open the interactive variable browser, optionally filtered.", Required = false)]
[CommandExample("vars", Title = "List visible variables")]
[CommandExample("vars path", Title = "Filter variable names")]
[CommandExample("vars browse env", Title = "Browse variables interactively")]
[CommandOutput("Records describing each binding in scope: name, kind (var/const/import), and current value.", ClrType = typeof(IAsyncEnumerable<ShellVariableEntry>))]
public sealed class VarsCommand : ShellCommand
{
    public VarsCommand()
        : base("vars", "Lists visible variables or opens an interactive variable browser.", "vars [filter] | vars browse [filter]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        await Task.CompletedTask;

        if (context.Arguments.Count > 0 &&
            context.Arguments[0]?.ToString() is { } subcommand &&
            string.Equals(subcommand, "browse", StringComparison.OrdinalIgnoreCase))
        {
            await foreach (var item in ExecuteBrowseAsync(context))
            {
                yield return item;
            }

            yield break;
        }

        var evaluator = context.Runtime.Evaluator;

        if (evaluator is null)
        {
            yield break;
        }

        var variables = evaluator.GetVisibleVariables();
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var filter = parsed.Positionals.Count > 0
            ? CommandArguments.RequireString(parsed.Positionals, 0, "filter")
            : null;

        foreach (var (name, value) in variables.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase))
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (filter is not null &&
                !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var typeName = value?.GetType().Name ?? "null";
            yield return new ShellVariableEntry(name, typeName, value);
        }
    }

    private static async IAsyncEnumerable<object?> ExecuteBrowseAsync(CommandContext context)
    {
        await Task.CompletedTask;

        var evaluator = context.Runtime.Evaluator;
        var entries = new List<ShellVariableBrowseEntry>();
        var filter = context.Arguments.Count > 1
            ? CommandArguments.RequireString(context.Arguments, 1, "filter")
            : null;

        entries.Add(new ShellVariableBrowseEntry(
            Section: "ToSh Runtime",
            Name: "$tosh",
            Type: context.Runtime.RuntimeNamespace?.GetType().Name ?? "object",
            Label: $"[runtime] $tosh",
            Expression: "$tosh",
            Value: context.Runtime.RuntimeNamespace));

        if (evaluator is not null)
        {
            foreach (var (name, value) in evaluator.GetVisibleVariables().OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase))
            {
                entries.Add(new ShellVariableBrowseEntry(
                    Section: "ToSh Variables",
                    Name: "$" + name,
                    Type: value?.GetType().Name ?? "null",
                    Label: $"[var] ${name}",
                    Expression: "$" + name,
                    Value: value));
            }
        }

        var env = Environment.GetEnvironmentVariables();
        foreach (var key in env.Keys.Cast<object?>().Select(static key => key?.ToString()).Where(static key => !string.IsNullOrWhiteSpace(key)).OrderBy(static key => key, StringComparer.OrdinalIgnoreCase))
        {
            entries.Add(new ShellVariableBrowseEntry(
                Section: "Environment Variables",
                Name: key!,
                Type: "string",
                Label: $"[env] {key}",
                Expression: "$env." + key,
                Value: Environment.GetEnvironmentVariable(key!)));
        }

        var filtered = string.IsNullOrWhiteSpace(filter)
            ? entries
            : entries.Where(entry =>
                    entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    entry.Section.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    entry.Label.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();

        if (filtered.Count == 0)
        {
            yield break;
        }

        if (context.Runtime.InlinePrompts is { } inline)
        {
            // Use the inline picker instead of inline filter because picker key handling
            // is currently more reliable across terminals.
            var selected = inline.Pick(
                filtered.Cast<object?>().ToArray(),
                prompt: "Browse variables (Enter to inspect)",
                displayProperty: nameof(ShellVariableBrowseEntry.Label),
                multiSelect: false,
                pageSize: 16);

            if (selected is { Count: > 0 } && selected[0] is ShellVariableBrowseEntry entry)
            {
                inline.Inspect(entry.Value, sourceExpression: entry.Expression);
                yield return entry;
            }

            yield break;
        }

        yield return new TuiPickRequest(
            filtered.Cast<object?>().ToArray(),
            DisplayProperty: nameof(ShellVariableBrowseEntry.Label),
            Prompt: "vars browse");
    }
}

public sealed record ShellVariableEntry(string Name, string Type, object? Value);

public sealed record ShellVariableBrowseEntry(
    string Section,
    string Name,
    string Type,
    string Label,
    string Expression,
    object? Value);
