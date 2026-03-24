namespace Tosh.Core;

public sealed class ShellCommandRegistry
{
    private readonly Dictionary<string, IShellCommand> _commands = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<IShellCommand> All => _commands.Values.OrderBy(command => command.Name, StringComparer.OrdinalIgnoreCase);

    public void Register(IShellCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!_commands.TryAdd(command.Name, command))
        {
            throw new InvalidOperationException($"A command named '{command.Name}' is already registered.");
        }
    }

    public void RegisterOrReplace(IShellCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _commands[command.Name] = command;
    }

    public bool TryGet(string name, out IShellCommand command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (_commands.TryGetValue(name, out var resolved))
        {
            command = resolved;
            return true;
        }

        command = null!;
        return false;
    }

    public IShellCommand Get(string name)
    {
        if (TryGet(name, out var command))
        {
            return command;
        }

        throw new InvalidOperationException($"Unknown command '{name}'.");
    }

    public bool Remove(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _commands.Remove(name);
    }
}
