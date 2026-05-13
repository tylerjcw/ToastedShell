using System.Collections.Concurrent;

namespace Tosh.Runtime;

/// <summary>
/// Registry for TSSP renderers — namespaced names mapping to a renderer
/// implementation (typically a Tosh function or a delegate). Exposed to
/// the shell as <c>$tosh.Config.Renderers</c> via the record-object
/// protocol so dot/bracket syntax both work.
/// </summary>
public sealed class ToshRenderersConfig : IResettableShellConfig, IShellRecordObject
{
    private readonly ConcurrentDictionary<string, object?> _entries = new(StringComparer.Ordinal);

    public string ShellTypeName => "TsspRenderers";

    public object? this[string name]
    {
        get => _entries.TryGetValue(name, out var v) ? v : null;
        set
        {
            if (value is null) _entries.TryRemove(name, out _);
            else _entries[name] = value;
        }
    }

    public bool Contains(string name) => _entries.ContainsKey(name);
    public IReadOnlyCollection<string> Names => _entries.Keys.ToArray();

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        if (_entries.TryGetValue(name, out value)) return true;
        value = null;
        return false;
    }

    public bool TrySetMember(string name, object? value)
    {
        if (value is null) _entries.TryRemove(name, out _);
        else _entries[name] = value;
        return true;
    }

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false) =>
        _entries.OrderBy(e => e.Key, StringComparer.Ordinal)
                .Select(e => new KeyValuePair<string, object?>(e.Key, e.Value))
                .ToArray();

    public void Reset() => _entries.Clear();
}

/// <summary>
/// Registry for TSSP schemas — namespaced names (e.g. <c>crumb.package</c>)
/// mapping to schema descriptors. Exposed as <c>$tosh.Config.Schemas</c>.
/// </summary>
public sealed class ToshSchemasConfig : IResettableShellConfig, IShellRecordObject
{
    private readonly ConcurrentDictionary<string, object?> _entries = new(StringComparer.Ordinal);

    public string ShellTypeName => "TsspSchemas";

    public object? this[string name]
    {
        get => _entries.TryGetValue(name, out var v) ? v : null;
        set
        {
            if (value is null) _entries.TryRemove(name, out _);
            else _entries[name] = value;
        }
    }

    public bool Contains(string name) => _entries.ContainsKey(name);
    public IReadOnlyCollection<string> Names => _entries.Keys.ToArray();

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        if (_entries.TryGetValue(name, out value)) return true;
        value = null;
        return false;
    }

    public bool TrySetMember(string name, object? value)
    {
        if (value is null) _entries.TryRemove(name, out _);
        else _entries[name] = value;
        return true;
    }

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false) =>
        _entries.OrderBy(e => e.Key, StringComparer.Ordinal)
                .Select(e => new KeyValuePair<string, object?>(e.Key, e.Value))
                .ToArray();

    public void Reset() => _entries.Clear();
}
