namespace Tosh.Core.Commands;

[CommandCategory("System")]
public sealed class VarsCommand : ShellCommand
{
    public VarsCommand()
        : base("vars", "Lists all visible variables in the current scope.", "vars [filter]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        await Task.CompletedTask;

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
}

public sealed record ShellVariableEntry(string Name, string Type, object? Value);
