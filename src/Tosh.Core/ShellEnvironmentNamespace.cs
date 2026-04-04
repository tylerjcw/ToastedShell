using System.Collections;

namespace Tosh.Core;

public sealed class ShellEnvironmentNamespace : IShellRecordObject
{
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
        return false;
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
