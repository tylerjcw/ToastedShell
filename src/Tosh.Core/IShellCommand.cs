namespace Tosh.Core;

public interface IShellCommand
{
    string Name { get; }

    string Description { get; }

    string Usage { get; }

    IAsyncEnumerable<object?> ExecuteAsync(CommandContext context);
}
