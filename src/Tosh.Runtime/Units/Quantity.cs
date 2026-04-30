using System.Globalization;

namespace Tosh.Runtime.Units;

/// <summary>
/// A numeric value with an associated unit of measurement.
/// This is the fundamental type for the unit system. Named wrapper types
/// (Length, Mass, etc.) inherit from this for common dimension categories.
/// </summary>
public class Quantity : IComparable, IComparable<Quantity>, IShellRecordObject, IFormattable
{
    public Quantity(double magnitude, UnitExpression dimension, string unitSymbol)
    {
        Magnitude = magnitude;
        Dimension = dimension;
        UnitSymbol = unitSymbol;
    }

    /// <summary>The numeric value in the user's original unit.</summary>
    public double Magnitude { get; }

    /// <summary>The SI base dimension expression (e.g. {Length:1, Time:-1} for m/s).</summary>
    public UnitExpression Dimension { get; }

    /// <summary>The unit symbol as the user typed it (e.g. "mph", "m/s", "kg").</summary>
    public string UnitSymbol { get; }

    /// <summary>
    /// Creates a Quantity from lexer-parsed components. The magnitude is the user's
    /// original value; routing through UnitRegistry to produce named types.
    /// </summary>
    public static Quantity FromParsed(double baseValue, double magnitude, UnitExpression dimension, string unitSymbol)
    {
        return UnitRegistry.Instance.CreateTyped(magnitude, dimension, unitSymbol);
    }

    /// <summary>The value converted to SI base units.</summary>
    public double BaseValue
    {
        get
        {
            var unit = UnitRegistry.Instance.TryResolve(UnitSymbol);
            return unit is not null ? unit.ToBase(Magnitude) : Magnitude;
        }
    }

    /// <summary>The category name for display (e.g. "Length", "Speed"). Override in named types.</summary>
    public virtual string CategoryName => UnitRegistry.Instance.GetCategoryForDimension(Dimension) ?? "Quantity";

    public string ShellTypeName => CategoryName;

    #region Arithmetic

    public static Quantity operator +(Quantity left, Quantity right)
    {
        EnsureCompatibleDimensions(left, right, "+");
        var rightConverted = ConvertToUnit(right, left.UnitSymbol);
        return left.WithMagnitude(left.Magnitude + rightConverted);
    }

    public static Quantity operator -(Quantity left, Quantity right)
    {
        EnsureCompatibleDimensions(left, right, "-");
        var rightConverted = ConvertToUnit(right, left.UnitSymbol);
        return left.WithMagnitude(left.Magnitude - rightConverted);
    }

    public static Quantity operator *(Quantity left, Quantity right)
    {
        var newDimension = left.Dimension.Multiply(right.Dimension);
        var newMagnitude = left.BaseValue * right.BaseValue;
        var newSymbol = CombineSymbols(left.UnitSymbol, right.UnitSymbol, "·");
        return UnitRegistry.Instance.CreateTyped(newMagnitude, newDimension, newSymbol, fromBase: true);
    }

    public static Quantity operator /(Quantity left, Quantity right)
    {
        if (right.Magnitude == 0) throw new DivideByZeroException("Cannot divide by zero quantity.");
        var newDimension = left.Dimension.Divide(right.Dimension);
        var newMagnitude = left.BaseValue / right.BaseValue;
        var newSymbol = CombineSymbols(left.UnitSymbol, right.UnitSymbol, "/");
        return UnitRegistry.Instance.CreateTyped(newMagnitude, newDimension, newSymbol, fromBase: true);
    }

    public static Quantity operator *(Quantity quantity, double scalar)
    {
        return quantity.WithMagnitude(quantity.Magnitude * scalar);
    }

    public static Quantity operator *(double scalar, Quantity quantity)
    {
        return quantity.WithMagnitude(scalar * quantity.Magnitude);
    }

    public static Quantity operator /(Quantity quantity, double scalar)
    {
        if (scalar == 0) throw new DivideByZeroException("Cannot divide by zero.");
        return quantity.WithMagnitude(quantity.Magnitude / scalar);
    }

    public static Quantity operator -(Quantity quantity)
    {
        return quantity.WithMagnitude(-quantity.Magnitude);
    }

    #endregion

    #region Comparison

    public int CompareTo(object? obj)
    {
        return obj switch
        {
            null => 1,
            Quantity q => CompareTo(q),
            _ => throw new ArgumentException($"Cannot compare Quantity with {obj.GetType().Name}."),
        };
    }

    public int CompareTo(Quantity? other)
    {
        if (other is null) return 1;
        EnsureCompatibleDimensions(this, other, "compare");
        return BaseValue.CompareTo(other.BaseValue);
    }

    #endregion

    #region Conversion

    /// <summary>Creates a new Quantity with the same unit but a different magnitude.</summary>
    public virtual Quantity WithMagnitude(double newMagnitude)
    {
        return UnitRegistry.Instance.CreateTyped(newMagnitude, Dimension, UnitSymbol);
    }

    /// <summary>
    /// Converts the right-hand Quantity to the left-hand unit for same-dimension arithmetic.
    /// Returns the converted magnitude in the target unit.
    /// </summary>
    private static double ConvertToUnit(Quantity source, string targetUnitSymbol)
    {
        var registry = UnitRegistry.Instance;
        var targetUnit = registry.TryResolve(targetUnitSymbol);
        if (targetUnit is null) return source.Magnitude;
        return targetUnit.FromBase(source.BaseValue);
    }

    #endregion

    #region IShellRecordObject

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        switch (name.ToLowerInvariant())
        {
            case "value" or "magnitude":
                value = Magnitude;
                return true;
            case "unit":
                value = UnitSymbol;
                return true;
            case "category":
                value = CategoryName;
                return true;
            case "dimension":
                value = Dimension.ToSymbolString();
                return true;
            case "base-value":
                value = BaseValue;
                return true;
            default:
                value = null;
                return false;
        }
    }

    public bool TrySetMember(string name, object? value) => false;

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        return
        [
            new("value", Magnitude),
            new("unit", UnitSymbol),
            new("category", CategoryName),
        ];
    }

    #endregion

    #region Formatting

    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        var mag = Magnitude.ToString("G", formatProvider ?? CultureInfo.InvariantCulture);
        return $"{mag} {UnitSymbol}";
    }

    public override string ToString()
    {
        return ToString(null, CultureInfo.InvariantCulture);
    }

    #endregion

    #region Helpers

    private static void EnsureCompatibleDimensions(Quantity left, Quantity right, string op)
    {
        if (left.Dimension != right.Dimension)
        {
            throw new InvalidOperationException(
                $"Cannot {op} {left.CategoryName} ({left.Dimension}) and {right.CategoryName} ({right.Dimension}).");
        }
    }

    private static string CombineSymbols(string left, string right, string op)
    {
        return $"{left}{op}{right}";
    }

    #endregion
}
