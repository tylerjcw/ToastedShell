using System.Collections;
using System.Dynamic;

namespace Tosh.Runtime;

public static class ShellRecordUtilities
{
    public static ExpandoObject CreateExpando(IEnumerable<KeyValuePair<string, object?>> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        IDictionary<string, object?> record = new ExpandoObject();

        foreach (var (name, value) in fields)
        {
            record[name] = value;
        }

        return (ExpandoObject)record;
    }

    public static bool IsRecordLike(object? value)
    {
        return value is IShellRecordObject or IReadOnlyDictionary<string, object?> or IDictionary<string, object?> or IDictionary;
    }

    public static bool TryGetFields(object? target, out IReadOnlyList<KeyValuePair<string, object?>> fields)
    {
        switch (target)
        {
            case null:
                fields = Array.Empty<KeyValuePair<string, object?>>();
                return false;

            case IShellRecordObject shellRecord:
                fields = shellRecord.GetMembers();
                return true;

            case IReadOnlyDictionary<string, object?> readOnlyDictionary:
                fields = readOnlyDictionary.ToArray();
                return true;

            case IDictionary<string, object?> dictionary:
                fields = dictionary.ToArray();
                return true;

            case IDictionary dictionary:
                fields = dictionary.Cast<DictionaryEntry>()
                    .Select(entry => new KeyValuePair<string, object?>(
                        entry.Key?.ToString() ?? string.Empty,
                        entry.Value))
                    .ToArray();
                return true;

            default:
                fields = Array.Empty<KeyValuePair<string, object?>>();
                return false;
        }
    }

    public static bool TryGetValue(object? target, string name, out object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (target is IShellRecordObject shellRecord && shellRecord.TryGetMember(name, out value))
        {
            return true;
        }

        if (target is IReadOnlyDictionary<string, object?> readOnlyDictionary &&
            TryGetDictionaryValue(readOnlyDictionary, name, out value))
        {
            return true;
        }

        if (target is IDictionary<string, object?> dictionary &&
            TryGetDictionaryValue(dictionary, name, out value))
        {
            return true;
        }

        if (target is IDictionary nonGenericDictionary &&
            TryGetDictionaryValue(nonGenericDictionary, name, out value))
        {
            return true;
        }

        value = null;
        return false;
    }

    public static bool TrySetValue(object target, string name, object? value)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (target is IShellRecordObject shellRecord)
        {
            return shellRecord.TrySetMember(name, value);
        }

        if (target is IDictionary<string, object?> dictionary)
        {
            var existingKey = dictionary.Keys.FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
            dictionary[existingKey ?? name] = value;
            return true;
        }

        if (target is IDictionary nonGenericDictionary)
        {
            object key = name;

            foreach (DictionaryEntry entry in nonGenericDictionary)
            {
                if (string.Equals(entry.Key?.ToString(), name, StringComparison.OrdinalIgnoreCase))
                {
                    key = entry.Key ?? name;
                    break;
                }
            }

            nonGenericDictionary[key] = value;
            return true;
        }

        return false;
    }

    private static bool TryGetDictionaryValue(
        IDictionary dictionary,
        string name,
        out object? value)
    {
        foreach (DictionaryEntry entry in dictionary)
        {
            if (string.Equals(entry.Key?.ToString(), name, StringComparison.OrdinalIgnoreCase))
            {
                value = entry.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static bool TryGetDictionaryValue(
        IEnumerable<KeyValuePair<string, object?>> dictionary,
        string name,
        out object? value)
    {
        foreach (var entry in dictionary)
        {
            if (string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = entry.Value;
                return true;
            }
        }

        value = null;
        return false;
    }
}
