namespace Tosh.Core;

public sealed class ShellCommandRegistry
{
    private readonly Dictionary<string, IShellCommand> _commands = new(StringComparer.Ordinal);

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

        // Suggest well-known corrections for common mistakes from other shells.
        var suggestion = name switch
        {
            "alias" => " Use 'func name => command' for aliases.",
            "unalias" => " Use 'forget name' to remove a function.",
            "set" => " Use 'var name = value' for variables, 'export NAME = \"value\"' for env vars.",
            "local" => " Use '$name = value' — variables are local by default.",
            "declare" or "typeset" => " Use '$name = value' for variables.",
            "readonly" => " Use 'const $name = value' for constants.",
            "test" or "[" => " Use 'if condition { ... }' with expression syntax.",
            _ => FindClosestCommand(name)
        };

        throw new InvalidOperationException($"Unknown command '{name}'.{suggestion}");
    }

    private string FindClosestCommand(string name)
    {
        var bestMatch = (Name: (string?)null, Distance: int.MaxValue);

        foreach (var candidate in _commands.Keys)
        {
            var distance = LevenshteinDistance(name, candidate);
            if (distance < bestMatch.Distance)
            {
                bestMatch = (candidate, distance);
            }
        }

        // Only suggest if the edit distance is reasonable (at most ~40% of the longer name).
        if (bestMatch.Name is not null && bestMatch.Distance <= Math.Max(2, Math.Max(name.Length, bestMatch.Name.Length) * 2 / 5))
        {
            return $" Did you mean '{bestMatch.Name}'?";
        }

        return string.Empty;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var costs = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) costs[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            var previousDiag = costs[0];
            costs[0] = i;

            for (var j = 1; j <= b.Length; j++)
            {
                var temp = costs[j];
                costs[j] = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1])
                    ? previousDiag
                    : Math.Min(Math.Min(costs[j - 1], costs[j]), previousDiag) + 1;
                previousDiag = temp;
            }
        }

        return costs[b.Length];
    }

    public bool Remove(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _commands.Remove(name);
    }
}
