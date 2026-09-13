using Tosh.Runtime;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tui.Declarative;

/// <summary>
/// One node of a declarative tree: the widget's name, its primary value, and whatever
/// else the author wrote alongside it.
/// </summary>
/// <remarks>
/// <para>
/// A node is written as a record, and the key that names a widget carries its main
/// argument — so <c>{| Text = "hello" |}</c>, <c>{| List = $items |}</c> and
/// <c>{| Row = [ … ] |}</c> all read as the thing they make. Everything else is a
/// property of it.
/// </para>
/// <para>
/// Reading is forgiving on purpose. A script has no types to lean on, so a width may
/// arrive as an <c>int</c>, a <c>long</c> or the string "12" depending on where it came
/// from, and refusing the last two would be pedantry rather than safety.
/// </para>
/// </remarks>
public sealed class TuiWidgetSpec
{
    private readonly IDictionary<string, object?> _fields;

    public TuiWidgetSpec(string name, object? primary, IDictionary<string, object?> fields)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(fields);

        Name = name;
        Primary = primary;
        _fields = fields;
    }

    /// <summary>The widget this node asks for.</summary>
    public string Name { get; }

    /// <summary>The value written against the widget's own key.</summary>
    public object? Primary { get; }

    /// <summary>Every key on the node, including the one that named the widget.</summary>
    public IReadOnlyDictionary<string, object?> Fields
        => _fields.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a key was written at all.</summary>
    public bool Has(string key) => TryGet(key, out _);

    /// <summary>Reads a key, case-insensitively.</summary>
    public bool TryGet(string key, out object? value)
    {
        foreach (var entry in _fields)
        {
            if (string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = entry.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    /// <summary>Reads a key as text.</summary>
    public string? Text(string key, string? fallback = null)
        => TryGet(key, out var value) && value is not null ? value.ToString() : fallback;

    /// <summary>Reads a key as a whole number, however it was written.</summary>
    public int Number(string key, int fallback)
    {
        if (!TryGet(key, out var value) || value is null)
        {
            return fallback;
        }

        return value switch
        {
            int number => number,
            long number => (int)number,
            double number => (int)number,
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => fallback,
        };
    }

    /// <summary>Reads a key as a flag, accepting the words a script might use.</summary>
    public bool Flag(string key, bool fallback = false)
    {
        if (!TryGet(key, out var value) || value is null)
        {
            return fallback;
        }

        return value switch
        {
            bool flag => flag,
            string text => ToshTruthiness.IsTruthy(text),
            _ => ToshTruthiness.IsTruthy(value),
        };
    }

    /// <summary>Reads a key as a list of values.</summary>
    /// <remarks>
    /// A single value counts as a list of one. Writing <c>List = "only"</c> and getting
    /// nothing would be a surprise nobody benefits from.
    /// </remarks>
    public IReadOnlyList<object?> Items(string key)
    {
        if (!TryGet(key, out var value) || value is null)
        {
            return [];
        }

        return AsItems(value);
    }

    /// <summary>The primary value read as a list.</summary>
    public IReadOnlyList<object?> PrimaryItems() => Primary is null ? [] : AsItems(Primary);

    private static IReadOnlyList<object?> AsItems(object value)
        => value is System.Collections.IEnumerable sequence and not string
            ? sequence.Cast<object?>().ToArray()
            : [value];

    /// <summary>Reads a key as a callable, for a handler or a pull binding.</summary>
    public IShellCallable? Callable(string key)
        => TryGet(key, out var value) ? value as IShellCallable : null;

    /// <summary>Reads a key as a length: a number of cells, "auto", or a star weight.</summary>
    /// <remarks>
    /// <c>"*"</c> and <c>"2*"</c> are the spellings a layout author expects, and a bare
    /// number means cells. Anything unrecognised is auto, which is the safe default: a
    /// child sized to its content is always drawable.
    /// </remarks>
    public TuiLength Length(string key, TuiLength fallback)
    {
        if (!TryGet(key, out var value) || value is null)
        {
            return fallback;
        }

        if (value is int cells)
        {
            return TuiLength.Fixed(cells);
        }

        var text = value.ToString()?.Trim() ?? string.Empty;

        if (text.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return TuiLength.Auto;
        }

        if (text.EndsWith('*'))
        {
            var weight = text[..^1];
            return TuiLength.Star(weight.Length == 0 ? 1 : int.TryParse(weight, out var parsed) ? parsed : 1);
        }

        return int.TryParse(text, out var fixedCells) ? TuiLength.Fixed(fixedCells) : fallback;
    }

    /// <summary>Reads the styling keys a node may carry.</summary>
    public TuiStyle Style()
    {
        var attributes = TuiTextAttributes.None;

        if (Flag("bold")) attributes |= TuiTextAttributes.Bold;
        if (Flag("dim")) attributes |= TuiTextAttributes.Dim;
        if (Flag("italic")) attributes |= TuiTextAttributes.Italic;
        if (Flag("underline")) attributes |= TuiTextAttributes.Underline;

        return new TuiStyle(Text("foreground"), Text("background"), attributes);
    }
}
