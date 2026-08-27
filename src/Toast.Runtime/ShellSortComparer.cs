namespace Tosh.Runtime;

/// <summary>
/// The total order the shell sorts by — `TOAST-0018`.
/// </summary>
/// <remarks>
/// <para>
/// This is a <em>total</em> order, and that is what separates it from the ordering the
/// language's <c>&lt;</c> implements. An operator may refuse a pair with no meaningful
/// order — a boolean against a boolean, a string against a number — and say so;
/// a sort may not, because every element has to land somewhere. So the two differ by
/// policy at the edges and must not differ anywhere else.
/// </para>
/// <para>
/// It lives in the runtime because it had three implementations. `SortCommand` held
/// this one, `ToshEngine` held a simplified copy for the fused <c>sort | first</c> path,
/// and the two disagreed: the copy compared only values of an identical type and fell
/// back to ordering by type <em>name</em>, so <c>[1, "a", 2.5] | sort</c> answered
/// <c>1, 2.5, "a"</c> while <c>| sort | first 3</c> answered <c>2.5, 1, "a"</c>. Sharing
/// the type is what makes that class of divergence unwritable rather than merely fixed.
/// </para>
/// <para>
/// **Strings compare by code point unless asked otherwise.** The default was
/// `OrdinalIgnoreCase`, which is friendlier to read and inconsistent with the rest of
/// the value model: equality holds two strings differing only in case to be *unequal*,
/// so an order calling them equal broke trichotomy — neither less, nor greater, nor
/// equal. Case-insensitive ordering is now opted into, by `sort -i`.
/// </para>
/// </remarks>
public sealed class ShellSortComparer : IComparer<object?>
{
    /// <summary>The default order: by code point, case-sensitively.</summary>
    public static readonly ShellSortComparer Ordinal = new();

    private readonly bool _numeric;
    private readonly bool _humanNumeric;
    private readonly StringComparer _strings;

    public ShellSortComparer(bool numeric = false, bool humanNumeric = false, bool ignoreCase = false)
    {
        _numeric = numeric;
        _humanNumeric = humanNumeric;
        _strings = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    }

    public int Compare(object? x, object? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        // `null` sorts first. The *operators* treat null as outside the order and answer
        // false in both directions; a sort cannot drop an element, so it gets a position.
        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        if (_humanNumeric)
        {
            var xText = x.ToString() ?? string.Empty;
            var yText = y.ToString() ?? string.Empty;

            if (StorageSize.TryParse(xText, out var xSize) && StorageSize.TryParse(yText, out var ySize))
            {
                return xSize.Bytes.CompareTo(ySize.Bytes);
            }
        }

        if (_numeric)
        {
            if (TryGetDouble(x, out var xNum) && TryGetDouble(y, out var yNum))
            {
                return xNum.CompareTo(yNum);
            }
        }

        if (x is string leftText && y is string rightText)
        {
            return _strings.Compare(leftText, rightText);
        }

        // Try to convert y to x's type for comparison, but skip the string
        // target type since TryConvert to string always succeeds via ToString()
        // and would give misleading ordinal comparisons for non-string types.
        if (x is IComparable comparable && x is not string &&
            x.GetType() != typeof(string) &&
            TypeConversion.TryConvert(y, x.GetType(), out var convertedY))
        {
            return comparable.CompareTo(convertedY);
        }

        if (y is IComparable reverseComparable && y is not string &&
            y.GetType() != typeof(string) &&
            TypeConversion.TryConvert(x, y.GetType(), out var convertedX))
        {
            return -reverseComparable.CompareTo(convertedX);
        }

        // When types are incompatible, group by type name first for
        // a stable and consistent ordering, then compare within groups.
        var xTypeName = x.GetType().Name;
        var yTypeName = y.GetType().Name;

        if (!string.Equals(xTypeName, yTypeName, StringComparison.Ordinal))
        {
            return string.Compare(xTypeName, yTypeName, StringComparison.Ordinal);
        }

        var leftString = x.ToString() ?? xTypeName;
        var rightString = y.ToString() ?? yTypeName;
        return _strings.Compare(leftString, rightString);
    }

    private static bool TryGetDouble(object value, out double result)
    {
        if (value is double d) { result = d; return true; }
        if (value is int i) { result = i; return true; }
        if (value is long l) { result = l; return true; }
        if (value is float f) { result = f; return true; }
        if (value is decimal m) { result = (double)m; return true; }

        if (value is string text && double.TryParse(text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out result))
        {
            return true;
        }

        result = 0;
        return false;
    }
}

/// <summary>Reverses any comparer, so the fused path need not hold a second one.</summary>
public sealed class ReverseComparer(IComparer<object?> inner) : IComparer<object?>
{
    public int Compare(object? x, object? y) => -inner.Compare(x, y);
}
