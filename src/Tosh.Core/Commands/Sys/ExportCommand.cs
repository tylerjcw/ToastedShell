namespace Tosh.Core.Commands.Sys;

[Stdlib(StdlibCategory.System)]
[CommandCategory("System")]
[CommandArgument("name", "The environment variable name.", Kind = "bareword")]
[CommandArgument("=", "Assignment operator.", Required = false, Kind = "bareword")]
[CommandArgument("value", "The value to assign. If omitted, exports the current Tosh variable of that name.", Required = false)]
[CommandExample("export PATH = \"/usr/local/bin:$env.PATH\"", Title = "Prepend to PATH")]
[CommandExample("export MY_VAR = \"hello\"", Title = "Set a new environment variable")]
[CommandExample("export MY_VAR", Title = "Export an existing Tosh variable")]
[CommandOutput("No output. The environment variable is set in the current process.", Mode = "none")]
public sealed class ExportCommand : ShellCommand
{
    public ExportCommand()
        : base("export", "Exports a Tosh value to the process environment.", "export <name> = <value>") { }

    public override IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count == 0 || context.Arguments.Count > 3)
        {
            throw new InvalidOperationException("Usage: export <name> = <value>, or export <name> to export an existing variable.");
        }

        var name = CommandArguments.RequireString(context.Arguments, 0, "variable name");

        if (string.IsNullOrWhiteSpace(name) || name.Contains('\0'))
        {
            throw new InvalidOperationException($"'{name}' is not a valid environment variable name.");
        }

        if (name.Contains('='))
        {
            throw new InvalidOperationException(
                $"'{name}' is not a valid environment variable name. Use: export NAME = \"value\"");
        }

        object? value;

        if (context.Arguments.Count == 3)
        {
            // New syntax: export NAME = value
            var eq = CommandArguments.RequireString(context.Arguments, 1, "=");
            if (eq != "=")
            {
                throw new InvalidOperationException(
                    $"Expected '=' but got '{eq}'. Usage: export {name} = \"value\"");
            }

            value = context.Arguments[2];
        }
        else if (context.Arguments.Count == 2)
        {
            // Legacy syntax: export NAME value (backward compatible)
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
