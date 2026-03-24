namespace Tosh.Core.Commands;

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

        object? value = context.Arguments.Count == 2
            ? context.Arguments[1]
            : context.Runtime.Variables.TryGetValue(name, out var existing)
                ? existing
                : throw new InvalidOperationException($"Variable '{name}' was not found.");
        var text = ExternalTextSerializer.Serialize(value);
        Environment.SetEnvironmentVariable(name, text);
        context.Runtime.Variables[name] = value;
        return AsyncEnumerableExtensions.FromEnumerable<object?>([new EnvironmentVariableEntry(name, text, IsSet: true)]);
    }
}
