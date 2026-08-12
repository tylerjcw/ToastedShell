using System.Globalization;

namespace Tosh.Runtime.Units;

/// <summary>
/// A numeric value with an associated unit of measurement.
/// This is the fundamental type for the unit system. Named wrapper types
/// (Length, Mass, etc.) inherit from this for common dimension categories.
/// </summary>
public class Quantity : IComparable, IComparable<Quantity>, IShellRecordObject, IFormattable
{
    private readonly UnitConversion _conversion;

    public Quantity(double magnitude, UnitExpression dimension, string unitSymbol)
    {
        Magnitude = magnitude;
        Dimension = dimension;
        UnitSymbol = unitSymbol;

        if (dimension.IsDimensionless && string.IsNullOrEmpty(unitSymbol))
        {
            _conversion = UnitConversion.Identity;
            return;
        }

        if (!UnitExpressionParser.TryParseConversion(
                unitSymbol,
                out var conversion,
                out var parsedDimension,
                out _) ||
            parsedDimension != dimension)
        {
            throw new ArgumentException(
                $"Unit '{unitSymbol}' does not describe dimension '{dimension}'.",
                nameof(unitSymbol));
        }

        _conversion = conversion;
    }

    /// <summary>The numeric value in the user's original unit.</summary>
    public double Magnitude { get; private set; }

    /// <summary>The SI base dimension expression (e.g. {Length:1, Time:-1} for m/s).</summary>
    public UnitExpression Dimension { get; }

    /// <summary>The unit symbol as the user typed it (e.g. "mph", "m/s", "kg").</summary>
    public string UnitSymbol { get; }

    /// <summary>
    /// Creates a Quantity from lexer-parsed components. The magnitude is the user's
    /// original value; routing through UnitRegistry to produce named types.
    /// </summary>
    public static Quantity FromParsed(double magnitude, UnitExpression dimension, string unitSymbol)
    {
        return UnitRegistry.Instance.CreateTyped(magnitude, dimension, unitSymbol);
    }

    /// <summary>Creates a typed quantity from a magnitude and a unit expression.</summary>
    public static Quantity FromLiteral(double magnitude, string unitSymbol)
    {
        if (!UnitExpressionParser.TryParseConversion(
                unitSymbol,
                out _,
                out var dimension,
                out var normalizedSymbol))
        {
            throw new FormatException($"Unknown or invalid unit expression '{unitSymbol}'.");
        }

        return UnitRegistry.Instance.CreateTyped(magnitude, dimension, normalizedSymbol);
    }

    /// <summary>The value converted to SI base units.</summary>
    public double BaseValue => _conversion.ToBase(Magnitude);

    /// <summary>Whether this value is an absolute temperature point.</summary>
    public bool IsAbsoluteTemperature =>
        Dimension == UnitExpression.Of(UnitDimension.Temperature);

    /// <summary>The category name for display (e.g. "Length", "Speed"). Override in named types.</summary>
    public virtual string CategoryName => UnitRegistry.Instance.GetCategoryForDimension(Dimension) ?? "Quantity";

    public string ShellTypeName => CategoryName;

    #region Arithmetic

    public static Quantity operator +(Quantity left, Quantity right)
    {
        EnsureCompatibleDimensions(left, right, "+");
        EnsureNotAbsoluteTemperature(left, "+");
        var rightConverted = left._conversion.FromBase(right.BaseValue);
        return left.WithMagnitude(left.Magnitude + rightConverted);
    }

    public static Quantity operator -(Quantity left, Quantity right)
    {
        EnsureCompatibleDimensions(left, right, "-");
        EnsureNotAbsoluteTemperature(left, "-");
        var rightConverted = left._conversion.FromBase(right.BaseValue);
        return left.WithMagnitude(left.Magnitude - rightConverted);
    }

    public static Quantity operator *(Quantity left, Quantity right)
    {
        var newDimension = left.Dimension.Multiply(right.Dimension);
        var newMagnitude = left.BaseValue * right.BaseValue;
        EnsureNotAbsoluteTemperature(left, "*");
        EnsureNotAbsoluteTemperature(right, "*");
        var newSymbol = UnitRegistry.Instance.GetCanonicalUnitSymbol(newDimension);
        return UnitRegistry.Instance.CreateTyped(newMagnitude, newDimension, newSymbol);
    }

    public static Quantity operator /(Quantity left, Quantity right)
    {
        EnsureNotAbsoluteTemperature(left, "/");
        EnsureNotAbsoluteTemperature(right, "/");
        if (right.BaseValue == 0) throw new DivideByZeroException("Cannot divide by zero quantity.");
        var newDimension = left.Dimension.Divide(right.Dimension);
        var newMagnitude = left.BaseValue / right.BaseValue;
        var newSymbol = UnitRegistry.Instance.GetCanonicalUnitSymbol(newDimension);
        return UnitRegistry.Instance.CreateTyped(newMagnitude, newDimension, newSymbol);
    }

    public static Quantity operator *(Quantity quantity, double scalar)
    {
        EnsureNotAbsoluteTemperature(quantity, "*");
        return quantity.WithMagnitude(quantity.Magnitude * scalar);
    }

    public static Quantity operator *(double scalar, Quantity quantity)
    {
        EnsureNotAbsoluteTemperature(quantity, "*");
        return quantity.WithMagnitude(scalar * quantity.Magnitude);
    }

    public static Quantity operator /(Quantity quantity, double scalar)
    {
        EnsureNotAbsoluteTemperature(quantity, "/");
        if (scalar == 0) throw new DivideByZeroException("Cannot divide by zero.");
        return quantity.WithMagnitude(quantity.Magnitude / scalar);
    }

    public static Quantity operator -(Quantity quantity)
    {
        EnsureNotAbsoluteTemperature(quantity, "negate");
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

    /// <summary>
    /// Value equality on the base value, matching <see cref="CompareTo(Quantity?)"/>.
    /// </summary>
    /// <remarks>
    /// Without this, ordering was dimension-aware while equality fell through to
    /// reference identity: <c>5`s &gt; 4000`ms</c> answered <see langword="true"/>
    /// while <c>5`s == 5000`ms</c> answered <see langword="false"/>, even though
    /// both render as <c>5 seconds</c>. The specification states that comparison
    /// operators use base-value comparison with dimension checking, and lists
    /// that exact equality as an example.
    ///
    /// Mismatched dimensions are unequal rather than an error — <c>==</c> is a
    /// question, where ordering is a request that has no meaningful answer across
    /// dimensions.
    /// </remarks>
    public override bool Equals(object? obj)
    {
        return obj is Quantity other &&
               Dimension.Equals(other.Dimension) &&
               BaseValue.Equals(other.BaseValue);
    }

    /// <summary>
    /// Hashes the base value and dimension, so quantities equal across units hash
    /// alike and can share a dictionary key or set slot.
    /// </summary>
    public override int GetHashCode() => HashCode.Combine(Dimension, BaseValue);

    #endregion

    #region Conversion

    /// <summary>Creates a new Quantity with the same unit but a different magnitude.</summary>
    public virtual Quantity WithMagnitude(double newMagnitude)
    {
        // Preserve the exact transform and named runtime subtype already carried
        // by this value. Reconstructing from UnitSymbol makes an existing value
        // change meaning after a user-unit registry removal/re-registration.
        var clone = (Quantity)MemberwiseClone();
        clone.Magnitude = newMagnitude;
        return clone;
    }

    /// <summary>Creates the same displayed quantity from a new base-unit value.</summary>
    public Quantity WithBaseValue(double newBaseValue) =>
        WithMagnitude(_conversion.FromBase(newBaseValue));

    /// <summary>
    /// Returns the same physical value displayed in <paramref name="targetUnitSymbol"/>.
    /// </summary>
    public Quantity To(string targetUnitSymbol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetUnitSymbol);

        if (!UnitExpressionParser.TryParseConversion(
                targetUnitSymbol,
                out var targetConversion,
                out var targetDimension,
                out var normalizedSymbol))
        {
            throw new InvalidOperationException($"Unknown or invalid target unit '{targetUnitSymbol}'.");
        }

        if (targetDimension != Dimension)
        {
            throw new InvalidOperationException(
                $"Cannot convert {CategoryName} ({Dimension}) to '{targetUnitSymbol}' ({targetDimension}).");
        }

        var targetMagnitude = targetConversion.FromBase(BaseValue);
        return UnitRegistry.Instance.CreateTyped(targetMagnitude, targetDimension, normalizedSymbol);
    }

    /// <summary>Descriptive alias for <see cref="To(string)"/>.</summary>
    public Quantity ConvertTo(string targetUnitSymbol) => To(targetUnitSymbol);

    /// <summary>
    /// Parses a quantity for script and CLR conversion boundaries. Both source-style
    /// <c>5`km</c> and argument-style <c>5km</c>/<c>5 km</c> are accepted here.
    /// </summary>
    public static bool TryParse(string? text, out Quantity quantity)
    {
        quantity = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var candidate = text.Trim();
        var backtick = candidate.IndexOf('`');

        if (backtick >= 0)
        {
            if (backtick == 0 || backtick == candidate.Length - 1 ||
                candidate.IndexOf('`', backtick + 1) >= 0)
            {
                return false;
            }

            return TryParseParts(candidate[..backtick], candidate[(backtick + 1)..], out quantity);
        }

        // Prefer the longest numeric prefix so exponent notation such as 1e3m
        // is not mistaken for magnitude 1 with unit e3m.
        for (var boundary = candidate.Length - 1; boundary > 0; boundary--)
        {
            if (TryParseParts(candidate[..boundary], candidate[boundary..], out quantity))
            {
                return true;
            }
        }

        return false;
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
            case "unit-expression":
                value = UnitSymbol;
                return true;
            case "canonical-unit":
                value = Dimension.ToCanonicalUnitSymbol();
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
            new("dimension", Dimension.ToSymbolString()),
            new("unit-expression", UnitSymbol),
            new("canonical-unit", Dimension.ToCanonicalUnitSymbol()),
            new("base-value", BaseValue),
        ];
    }

    #endregion

    #region Formatting

    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        // Quantities are engineering-facing display values. Fifteen significant
        // digits suppress ordinary binary64 arithmetic noise (for example,
        // 48 * 10.3 displaying as 494.40000000000003) while an explicit format
        // such as "R" remains available when round-trip text is required.
        var numericFormat = string.IsNullOrEmpty(format) ? "G15" : format;
        var mag = Magnitude.ToString(numericFormat, formatProvider ?? CultureInfo.InvariantCulture);
        return string.IsNullOrEmpty(UnitSymbol) ? mag : $"{mag} {UnitSymbol}";
    }

    /// <summary>Formats the magnitude with invariant culture and retains the unit symbol.</summary>
    public string ToString(string? format) => ToString(format, CultureInfo.InvariantCulture);

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

    private static void EnsureNotAbsoluteTemperature(Quantity quantity, string op)
    {
        if (quantity.IsAbsoluteTemperature)
        {
            throw new InvalidOperationException(
                $"Cannot {op} an absolute temperature until temperature-difference units are explicit; convert or compare it instead.");
        }
    }

    private static bool TryParseParts(string magnitudeText, string unitText, out Quantity quantity)
    {
        quantity = null!;
        magnitudeText = magnitudeText.Trim();
        unitText = unitText.Trim();

        if (magnitudeText.Length == 0 || unitText.Length == 0 ||
            !HasValidNumericSeparators(magnitudeText))
        {
            return false;
        }

        var numericText = magnitudeText.Contains('_')
            ? magnitudeText.Replace("_", string.Empty, StringComparison.Ordinal)
            : magnitudeText;

        if (!double.TryParse(
                numericText,
                NumberStyles.Float | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var magnitude) ||
            !UnitExpressionParser.TryParseConversion(
                unitText,
                out _,
                out var dimension,
                out var normalizedSymbol))
        {
            return false;
        }

        quantity = FromParsed(magnitude, dimension, normalizedSymbol);
        return true;
    }

    private static bool HasValidNumericSeparators(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '_') continue;
            if (i == 0 || i + 1 >= text.Length ||
                !char.IsAsciiDigit(text[i - 1]) ||
                !char.IsAsciiDigit(text[i + 1]))
            {
                return false;
            }
        }

        return true;
    }

    #endregion
}
