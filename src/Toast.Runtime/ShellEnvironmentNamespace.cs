using System.Collections;

namespace Tosh.Runtime;

public sealed class ShellEnvironmentNamespace : IShellRecordObject
{
    private readonly IToastEnvironmentExporter? _exporter;

    public ShellEnvironmentNamespace(IToastEnvironmentExporter? exporter = null)
    {
        _exporter = exporter;
    }

    public string ShellTypeName => "Environment";

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        value = Environment.GetEnvironmentVariable(name);
        if (value is not null)
        {
            return true;
        }

        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (string.Equals(entry.Key?.ToString(), name, StringComparison.OrdinalIgnoreCase))
            {
                value = entry.Value?.ToString();
                return true;
            }
        }

        // Environment lookups are value-oriented. Missing members resolve to null
        // instead of surfacing a missing-member error.
        value = null;
        return true;
    }

    public bool TrySetMember(string name, object? value)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        // Resolve the canonical case of an existing variable so $env.path = "x"
        // updates PATH rather than creating a separate "path" entry.
        var canonical = name;
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = entry.Key?.ToString();
            if (key is not null && string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                canonical = key;
                break;
            }
        }

        if (_exporter is not null)
        {
            // Let TōSh track the name as exported and mirror it into the session's
            // Variables dictionary, matching `export NAME = …`.
            _exporter.ExportEnvironmentVariable(canonical, value);
        }
        else
        {
            Environment.SetEnvironmentVariable(canonical, ExternalTextSerializer.Serialize(value));
        }
        return true;
    }

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        var members = new List<KeyValuePair<string, object?>>();

        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = entry.Key?.ToString();
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            members.Add(new KeyValuePair<string, object?>(key, entry.Value?.ToString()));
        }

        members.Sort(static (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Key, right.Key));
        return members;
    }
}
