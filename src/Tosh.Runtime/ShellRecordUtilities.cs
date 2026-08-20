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

    /// <summary>Reserved key carrying per-record render hints attached by
    /// the TSSP consumer (e.g. a precomputed title string from a schema's
    /// <c>title</c> template). Hidden from column inference and standard
    /// table rendering; consulted by DisplayEngine for single-record
    /// titling.</summary>
    public const string TsspMetaKey = "__tssp_meta";

    /// <summary>Returns true when a field key should not be surfaced as a
    /// column. Currently filters the TSSP meta sentinel.</summary>
    public static bool IsHiddenField(string key)
        => string.Equals(key, TsspMetaKey, StringComparison.Ordinal);

    /// <summary>Reads the optional <c>title</c> string from a record's
    /// TSSP meta sentinel, if present.</summary>
    public static string? TryGetTsspTitle(object? target)
    {
        if (!TryGetValue(target, TsspMetaKey, out var meta) || meta is null) return null;
        if (!TryGetValue(meta, "title", out var t)) return null;
        return t as string;
    }

    /// <summary>Same as <see cref="TryGetFields"/> but filters reserved
    /// sentinel keys (e.g. <see cref="TsspMetaKey"/>) so column-inference
    /// callers do not surface them as user-visible columns.</summary>
    public static bool TryGetVisibleFields(object? target, out IReadOnlyList<KeyValuePair<string, object?>> fields)
    {
        if (!TryGetFields(target, out var raw))
        {
            fields = raw;
            return false;
        }

        if (!raw.Any(f => IsHiddenField(f.Key)))
        {
            fields = raw;
            return true;
        }

        fields = raw.Where(f => !IsHiddenField(f.Key)).ToArray();
        return true;
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
                // `Cast<DictionaryEntry>()` enumerates through IEnumerable, and
                // Dictionary<K,V> yields boxed KeyValuePair<K,V> there — only its explicit
                // IDictionary.GetEnumerator() yields DictionaryEntry. So a `{% … %}` literal,
                // which is object-keyed, crashed this with a raw InvalidCastException surfacing
                // as `unexpected_exception` (TS-P1-29). Going through IDictionaryEnumerator
                // handles both shapes, because IDictionary.GetEnumerator() is defined to return
                // one whatever the concrete dictionary is.
                var entries = new List<KeyValuePair<string, object?>>(dictionary.Count);
                var enumerator = dictionary.GetEnumerator();

                try
                {
                    while (enumerator.MoveNext())
                    {
                        entries.Add(new KeyValuePair<string, object?>(
                            enumerator.Key?.ToString() ?? string.Empty,
                            enumerator.Value));
                    }
                }
                finally
                {
                    // IDictionaryEnumerator does not extend IDisposable, but concrete ones
                    // often implement it; Hashtable's does.
                    (enumerator as IDisposable)?.Dispose();
                }

                fields = entries;
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
            // `TOAST-0018`. The key must *be* a name, not merely render as one. Matching
            // `entry.Key?.ToString()` meant an `Int32` key of `1` answered to `"1"`, which
            // is coercion — and it left `$d["1"]` order-dependent in a dictionary holding
            // both `1` and `"1"`. Case-insensitivity stays: reaching a field by name is
            // what this overload is for.
            if (entry.Key is string keyText &&
                string.Equals(keyText, name, StringComparison.OrdinalIgnoreCase))
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
