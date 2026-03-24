namespace Tosh.Core;

public abstract class ShellCommand : IShellCommand
{
    protected ShellCommand(string name, string description, string usage)
    {
        Name = name;
        Description = description;
        Usage = usage;
    }

    public string Name { get; }

    public string Description { get; }

    public string Usage { get; }

    public abstract IAsyncEnumerable<object?> ExecuteAsync(CommandContext context);
}
