namespace Tosh.Runtime;

public sealed class ShellCommandRegistry : IScopedCommandView
{
    private readonly Dictionary<string, IShellCommand> _commands = new(StringComparer.Ordinal);
    // alias name -> canonical command name. Aliases are additional invocation names that
    // resolve to an already-registered canonical command. Surfaced in help under the canonical entry.
    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);

    /// <summary>
    /// Enumerates only canonical commands (aliases are excluded). Use <see cref="GetAliases"/>
    /// to discover the alias names that resolve to each canonical command.
    /// </summary>
    public IEnumerable<IShellCommand> All => _commands.Values.OrderBy(command => command.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Enumerates every name that resolves to a command, canonical names and aliases alike.
    /// </summary>
    public IEnumerable<string> AllNames =>
        _commands.Keys.Concat(_aliases.Keys).OrderBy(name => name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when a word could plausibly be a misspelled command name: it starts with a letter
    /// or underscore, and every other character is one a command name can contain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both of the shell's "did you mean" paths ask this before consulting edit distance, which
    /// otherwise answers any question it is asked: a bare <c>~</c> is one edit from a command
    /// called <c>f</c>, and was duly offered as a correction for it. Punctuation is not a
    /// misspelling of anything, and a suggestion for it makes the diagnostic read as though the
    /// shell had understood something subtler than it has.
    /// </para>
    /// <para>
    /// It lives here, on the registry both callers already hold, because the rule existing twice
    /// is how one of them would come to disagree with the other (<c>TS-P1-24</c>).
    /// </para>
    /// <para>
    /// Deliberately permissive about the interior — real command names hold digits, <c>-</c>,
    /// <c>_</c>, <c>.</c> and <c>:</c> — and strict about the first character, which is what
    /// separates a name from punctuation.
    /// </para>
    /// </remarks>
    public static bool IsNameShaped(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (!char.IsLetter(name[0]) && name[0] != '_') return false;

        foreach (var character in name)
        {
            if (!char.IsLetterOrDigit(character) && character is not ('_' or '-' or '.' or ':'))
            {
                return false;
            }
        }

        return true;
    }

    public void Register(IShellCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (_aliases.ContainsKey(command.Name))
        {
            throw new InvalidOperationException($"The name '{command.Name}' is already registered as an alias.");
        }

        if (!_commands.TryAdd(command.Name, command))
        {
            throw new InvalidOperationException($"A command named '{command.Name}' is already registered.");
        }
    }

    public void RegisterOrReplace(IShellCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _aliases.Remove(command.Name);
        _commands[command.Name] = command;
    }

    /// <summary>
    /// Registers <paramref name="aliasName"/> as another invocation name for an already-registered
    /// canonical command. The alias resolves via <see cref="TryGet"/> and surfaces in help under
    /// the canonical command's entry. The canonical command must be registered first.
    /// </summary>
    public void RegisterAlias(string aliasName, string canonicalName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aliasName);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalName);

        if (!_commands.ContainsKey(canonicalName))
        {
            throw new InvalidOperationException(
                $"Cannot register alias '{aliasName}' for unknown command '{canonicalName}'. Register the canonical command first.");
        }

        if (_commands.ContainsKey(aliasName))
        {
            throw new InvalidOperationException($"Cannot register alias '{aliasName}': a command with that name already exists.");
        }

        if (_aliases.TryGetValue(aliasName, out var existing))
        {
            if (string.Equals(existing, canonicalName, StringComparison.Ordinal))
            {
                return; // idempotent
            }
            throw new InvalidOperationException(
                $"Alias '{aliasName}' is already registered against '{existing}'.");
        }

        _aliases[aliasName] = canonicalName;
    }

    /// <summary>
    /// Returns alias names that resolve to <paramref name="canonicalName"/>, sorted ordinal-ignore-case.
    /// </summary>
    public IReadOnlyList<string> GetAliases(string canonicalName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalName);
        return _aliases
            .Where(kvp => string.Equals(kvp.Value, canonicalName, StringComparison.Ordinal))
            .Select(kvp => kvp.Key)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Returns a snapshot map of canonical-name -> alias names. Includes only canonicals that have
    /// at least one alias registered against them.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetAliasMap()
    {
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in _aliases.GroupBy(kvp => kvp.Value, StringComparer.Ordinal))
        {
            map[group.Key] = group
                .Select(kvp => kvp.Key)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        return map;
    }

    public bool TryGet(string name, out IShellCommand command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (_commands.TryGetValue(name, out var resolved))
        {
            command = resolved;
            return true;
        }

        if (_aliases.TryGetValue(name, out var canonical) &&
            _commands.TryGetValue(canonical, out var aliasResolved))
        {
            command = aliasResolved;
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
        if (_aliases.Remove(name))
        {
            return true;
        }
        if (_commands.Remove(name))
        {
            // Drop any aliases that pointed at the removed canonical.
            foreach (var alias in _aliases.Where(kvp => string.Equals(kvp.Value, name, StringComparison.Ordinal)).Select(kvp => kvp.Key).ToArray())
            {
                _aliases.Remove(alias);
            }
            return true;
        }
        return false;
    }
}
