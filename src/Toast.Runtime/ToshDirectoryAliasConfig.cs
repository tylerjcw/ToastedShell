namespace Tosh.Runtime;

// `TOAST-0006`. Read by language-side path resolution, so it travels with the language;
// the shell still owns loading it from configuration.

public sealed class ToshDirectoryAliasConfig : IResettableShellConfig, IShellRecordObject
{
    private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);

    public string ShellTypeName => "DirectoryAliases";

    public IReadOnlyDictionary<string, string> Aliases => _aliases;

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        if (_aliases.TryGetValue(name, out var path))
        {
            value = path;
            return true;
        }

        value = null;
        return false;
    }

    public bool TrySetMember(string name, object? value)
    {
        if (value is null)
        {
            _aliases.Remove(name);
            return true;
        }

        var path = value.ToString();

        if (string.IsNullOrWhiteSpace(path))
        {
            _aliases.Remove(name);
            return true;
        }

        _aliases[name] = Path.GetFullPath(path);
        return true;
    }

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        return _aliases
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new KeyValuePair<string, object?>(entry.Key, entry.Value))
            .ToArray();
    }

    public bool TryResolve(string name, out string resolvedPath)
    {
        return _aliases.TryGetValue(name, out resolvedPath!);
    }

    public string? TryReverseLookup(string absolutePath)
    {
        string? bestAlias = null;
        var bestLength = 0;

        foreach (var (alias, aliasPath) in _aliases)
        {
            if (absolutePath.Equals(aliasPath, PathUtilities.GetPathComparison()) ||
                (absolutePath.StartsWith(aliasPath, PathUtilities.GetPathComparison()) &&
                 absolutePath.Length > aliasPath.Length &&
                 absolutePath[aliasPath.Length] == Path.DirectorySeparatorChar))
            {
                if (aliasPath.Length > bestLength)
                {
                    bestLength = aliasPath.Length;
                    bestAlias = alias;
                }
            }
        }

        return bestAlias;
    }

    public void Reset()
    {
        _aliases.Clear();
    }
}

