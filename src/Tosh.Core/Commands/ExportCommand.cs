namespace Tosh.Core.Commands;

[CommandCategory("System")]
public sealed class ExportCommand : ShellCommand
{
    public ExportCommand()
        : base("export", "Exports a Tosh value to the process environment.", "export <name> [value]") { }

    public override IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count == 0 || context.Arguments.Count > 2)
        {
            throw new InvalidOperationException("export expects a variable name and an optional value.");
        }

        var name = CommandArguments.RequireString(context.Arguments, 0, "variable name");

        if (string.IsNullOrWhiteSpace(name) || name.Contains('=') || name.Contains('\0'))
        {
            throw new InvalidOperationException($"'{name}' is not a valid environment variable name.");
        }

        object? value;

        if (context.Arguments.Count == 2)
        {
            value = context.Arguments[1];
        }
        else if (context.Runtime.Evaluator?.TryGetVariableValue(name, out var existing) == true)
        {
            value = existing;
        }
        else if (context.Runtime.Variables.TryGetValue(name, out existing))
        {
            value = existing;
        }
        else
        {
            throw new InvalidOperationException($"Variable '{name}' was not found.");
        }

        context.Runtime.ExportEnvironmentVariable(name, value);
        var text = ExternalTextSerializer.Serialize(value);
        return AsyncEnumerableExtensions.FromEnumerable<object?>([new EnvironmentVariableEntry(name, text, IsSet: true)]);
    }
}
