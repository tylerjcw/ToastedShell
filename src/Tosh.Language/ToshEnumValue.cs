using Tosh.Runtime;

namespace Tosh.Language;

public readonly record struct ToshEnumValue(ToshEnumDefinition Definition, string Name, object? UnderlyingValue)
    : IShellTypedObject, IFormattable, IShellTypeCheckable, IShellEnumValue, IComparable<ToshEnumValue>, IComparable
{
    public IShellTypeDescriptor ShellTypeDescriptor => Definition;

    public string EnumTypeName => Definition.Name;

    public override string ToString() => Name;

    public string ToString(string? format, IFormatProvider? formatProvider) => Name;

    public bool IsInstanceOf(string typeName)
    {
        return string.Equals(Definition.Name, typeName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Orders members by their backing value so `E.Low &lt; E.High`,
    /// `sort`, `min`, and `max` behave as a numeric-backed enum should
    /// (TS-P1-15). Members of different enums are not ordered against
    /// each other.
    /// </summary>
    public int CompareTo(ToshEnumValue other)
    {
        if (!string.Equals(EnumTypeName, other.EnumTypeName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Enum members of '{EnumTypeName}' and '{other.EnumTypeName}' cannot be ordered against each other.");
        }

        return ShellEnumComparison.CompareUnderlying(UnderlyingValue, other.UnderlyingValue);
    }

    public int CompareTo(object? obj)
    {
        if (obj is ToshEnumValue other)
        {
            return CompareTo(other);
        }

        // Ordering against the backing value keeps `E.Mid > 0` working
        // for numeric-backed enums.
        return ShellEnumComparison.CompareUnderlying(UnderlyingValue, obj);
    }
}
