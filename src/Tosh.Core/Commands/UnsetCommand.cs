namespace Tosh.Core.Commands;

public sealed class UnsetCommand : ShellCommand
{
    public UnsetCommand()
        : base("unset", "Removes Tosh variables and process environment variables.", "unset <name> [name...]") { }

    public override IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count == 0)
        {
            throw new InvalidOperationException("unset expects at least one variable name.");
        }

        var results = new List<object?>();

        foreach (var name in context.Arguments.Select((_, index) => CommandArguments.RequireString(context.Arguments, index, "variable name")))
        {
            context.Runtime.Variables.Remove(name);
            Environment.SetEnvironmentVariable(name, null);
            results.Add(new EnvironmentVariableEntry(name, null, IsSet: false));
        }

        return AsyncEnumerableExtensions.FromEnumerable(results);
    }
}
