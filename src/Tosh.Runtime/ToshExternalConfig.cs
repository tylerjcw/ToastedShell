namespace Tosh.Runtime;

/// <summary>
/// Configuration for how external (non-builtin) programs are launched.
/// Exposed to the shell as <c>$tosh.Config.External</c>.
/// </summary>
public sealed class ToshExternalConfig : IResettableShellConfig, IShellRecordObject
{
    // First-party TōSh consumers that ship with the toolchain and natively
    // emit TSSP. Seeded into HybridConsumers on construction and restored on
    // Reset(). Users can still Remove() any of these from their own config.
    private static readonly string[] DefaultHybridConsumers = ["crumb"];

    private readonly ToshExternalConsumerList _hybrid = new(DefaultHybridConsumers);

    public string ShellTypeName => "External";

    /// <summary>
    /// Bare program names (no path, no extension) that should be launched in
    /// hybrid mode: stdout piped to ToSh for TSSP parsing, stdin/stderr
    /// inherited, child placed in its own pgrp with terminal foreground
    /// handoff. Empty by default — programs must opt in.
    /// </summary>
    public ToshExternalConsumerList HybridConsumers => _hybrid;

    public bool IsHybridConsumer(string bareName) => _hybrid.Contains(bareName);

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        if (string.Equals(name, nameof(HybridConsumers), StringComparison.Ordinal))
        {
            value = _hybrid;
            return true;
        }

        value = null;
        return false;
    }

    public bool TrySetMember(string name, object? value) => false;

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false) =>
        new[] { new KeyValuePair<string, object?>(nameof(HybridConsumers), (object?)_hybrid) };

    public void Reset() => _hybrid.Reset();
}

/// <summary>
/// Case-sensitive set of bare program names. Surfaces an Add/Remove/Contains
/// API for use from .tosh scripts.
/// </summary>
public sealed class ToshExternalConsumerList : IResettableShellConfig
{
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    private readonly string[] _defaults;

    public ToshExternalConsumerList() : this(Array.Empty<string>()) { }

    public ToshExternalConsumerList(IEnumerable<string> defaults)
    {
        _defaults = defaults.ToArray();
        foreach (var d in _defaults) _names.Add(d);
    }

    public bool Add(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return _names.Add(name);
    }

    public bool Remove(string name) => _names.Remove(name);

    public bool Contains(string name) => _names.Contains(name);

    public int Count => _names.Count;

    public IReadOnlyCollection<string> Names => _names;

    public override string ToString() =>
        _names.Count == 0 ? "[]" : "[" + string.Join(", ", _names.OrderBy(n => n, StringComparer.Ordinal)) + "]";

    public void Reset()
    {
        _names.Clear();
        foreach (var d in _defaults) _names.Add(d);
    }
}
