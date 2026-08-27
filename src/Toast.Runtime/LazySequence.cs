namespace Tosh.Runtime;

/// <summary>
/// A lazily-evaluated, memoized sequence. Items are computed on demand and cached
/// so that re-traversal does not re-execute the generator.
/// </summary>
public sealed class LazySequence : IShellEnumerableObject
{
    private readonly IEnumerator<object?>? _sourceEnumerator;
    private readonly List<object?> _cache = new();
    private bool _exhausted;
    private readonly object _lock = new();
    private readonly string? _label;

    public LazySequence(IEnumerable<object?> source, string? label = null)
    {
        _sourceEnumerator = source.GetEnumerator();
        _label = label;
    }

    /// <summary>
    /// Creates a LazySequence from an already-materialized list (fully cached, no generator).
    /// </summary>
    public LazySequence(IReadOnlyList<object?> items, string? label = null)
    {
        _cache.AddRange(items);
        _exhausted = true;
        _sourceEnumerator = null;
        _label = label;
    }

    /// <summary>
    /// True only when the sequence is known to be fully materialized and finite.
    /// A LazySequence wrapping an unevaluated generator returns false (unknown).
    /// </summary>
    public bool IsFiniteKnown => _exhausted;

    public IEnumerable<object?> EnumerateShellItems()
    {
        var index = 0;

        while (true)
        {
            object? item;
            bool hasItem;

            lock (_lock)
            {
                if (index < _cache.Count)
                {
                    item = _cache[index];
                    hasItem = true;
                }
                else if (_exhausted)
                {
                    yield break;
                }
                else
                {
                    // Advance the source enumerator
                    if (_sourceEnumerator!.MoveNext())
                    {
                        item = _sourceEnumerator.Current;
                        _cache.Add(item);
                        hasItem = true;
                    }
                    else
                    {
                        _exhausted = true;
                        hasItem = false;
                        item = null;
                    }
                }
            }

            if (!hasItem)
            {
                yield break;
            }

            yield return item;
            index++;
        }
    }

    /// <summary>
    /// Returns the number of items computed so far without forcing more evaluation.
    /// </summary>
    public int CachedCount
    {
        get { lock (_lock) { return _cache.Count; } }
    }

    /// <summary>
    /// Whether the underlying source has been fully consumed.
    /// </summary>
    public bool IsExhausted
    {
        get { lock (_lock) { return _exhausted; } }
    }

    public override string ToString()
    {
        lock (_lock)
        {
            var preview = string.Join(", ", _cache.Take(5).Select(x => x?.ToString() ?? "null"));
            var suffix = _exhausted
                ? (_cache.Count > 5 ? ", ..." : "")
                : ", ...";
            var name = _label is not null ? $"{_label} " : "";
            return $"lazy {name}[{preview}{suffix}]";
        }
    }
}
