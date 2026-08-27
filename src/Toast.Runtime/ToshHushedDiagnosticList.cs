using System.Collections;

namespace Tosh.Runtime;

// `TOAST-0006`. `hush` is a language feature, so the list of hushed codes travels with
// the language even though the shell is what loads it from configuration.

public sealed class ToshHushedDiagnosticList : IResettableShellConfig, IEnumerable<string>
{
    private readonly HashSet<string> _codes = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _codes.Count;

    public bool Contains(string code) => _codes.Contains(code);

    public bool Add(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return _codes.Add(code.Trim());
    }

    public bool Remove(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return _codes.Remove(code.Trim());
    }

    public void Clear() => _codes.Clear();

    public IEnumerator<string> GetEnumerator()
    {
        return _codes
            .OrderBy(static c => c, StringComparer.OrdinalIgnoreCase)
            .GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public IReadOnlyCollection<string> ToReadOnly()
    {
        return _codes
            .OrderBy(static c => c, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void Reset() => _codes.Clear();

    public override string ToString() => $"[{string.Join(", ", this)}]";
}
