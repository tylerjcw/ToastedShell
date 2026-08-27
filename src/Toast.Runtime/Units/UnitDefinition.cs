namespace Tosh.Runtime.Units;

/// <summary>
/// Describes how a unit participates in physical arithmetic. A temperature
/// point has an origin even when its numeric offset happens to be zero (Kelvin
/// and Rankine), so offset alone cannot distinguish it from a linear unit.
/// </summary>
public enum UnitRole
{
    Linear,
    AbsoluteTemperature,
}

/// <summary>
/// Describes how a unit converts to its SI base.
/// For most units this is a simple scale factor; temperature units also need an offset.
/// </summary>
public sealed class UnitDefinition
{
    public UnitDefinition(
        string symbol,
        string name,
        string category,
        UnitExpression dimension,
        double toBaseFactor,
        double toBaseOffset = 0.0,
        bool isUserDefined = false,
        bool allowSiPrefixes = false,
        UnitRole role = UnitRole.Linear)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("A unit symbol cannot be blank.", nameof(symbol));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A unit name cannot be blank.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(category))
        {
            throw new ArgumentException("A unit category cannot be blank.", nameof(category));
        }

        ArgumentNullException.ThrowIfNull(dimension);

        if (!double.IsFinite(toBaseFactor) || toBaseFactor <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(toBaseFactor),
                "A unit conversion factor must be finite and positive.");
        }

        if (!double.IsFinite(toBaseOffset))
        {
            throw new ArgumentOutOfRangeException(
                nameof(toBaseOffset),
                "A unit conversion offset must be finite.");
        }

        if (allowSiPrefixes && toBaseOffset != 0.0)
        {
            throw new ArgumentException(
                "An affine unit cannot synthesize SI-prefixed variants.",
                nameof(allowSiPrefixes));
        }

        if (toBaseOffset != 0.0 && role != UnitRole.AbsoluteTemperature)
        {
            throw new ArgumentException(
                "An offset conversion must declare an explicit point-unit role.",
                nameof(role));
        }

        if (role == UnitRole.AbsoluteTemperature &&
            dimension != UnitExpression.Of(UnitDimension.Temperature))
        {
            throw new ArgumentException(
                "Only temperature dimensions can use the absolute-temperature role.",
                nameof(role));
        }

        Symbol = symbol;
        Name = name;
        Category = category;
        Dimension = dimension;
        ToBaseFactor = toBaseFactor;
        ToBaseOffset = toBaseOffset;
        IsUserDefined = isUserDefined;
        AllowSiPrefixes = allowSiPrefixes;
        Role = role;
    }

    /// <summary>Short symbol used in literals and display (e.g. "m", "kg", "mph").</summary>
    public string Symbol { get; }

    /// <summary>Human-readable name (e.g. "meter", "kilogram").</summary>
    public string Name { get; }

    /// <summary>Grouping category (e.g. "Length", "Mass", "Duration").</summary>
    public string Category { get; }

    /// <summary>The SI base dimension expression for this unit.</summary>
    public UnitExpression Dimension { get; }

    /// <summary>
    /// Multiply a value in this unit by this factor to get the value in SI base units.
    /// For temperature, apply as: base_value = value * ToBaseFactor + ToBaseOffset.
    /// </summary>
    public double ToBaseFactor { get; }

    /// <summary>
    /// Additive offset for converting to SI base (non-zero only for temperature).
    /// </summary>
    public double ToBaseOffset { get; }

    /// <summary>Whether this unit was defined by the user at runtime.</summary>
    public bool IsUserDefined { get; }

    /// <summary>Whether an SI prefix may be synthesized before this symbol.</summary>
    public bool AllowSiPrefixes { get; }

    /// <summary>How this unit participates in arithmetic.</summary>
    public UnitRole Role { get; }

    /// <summary>Whether this unit requires offset conversion (temperature).</summary>
    public bool HasOffset => ToBaseOffset != 0.0;

    /// <summary>The immutable display-to-base transform for this unit.</summary>
    public UnitConversion Conversion => new(ToBaseFactor, ToBaseOffset);

    /// <summary>Convert a value from this unit to SI base units.</summary>
    public double ToBase(double value) => Conversion.ToBase(value);

    /// <summary>Convert a value from SI base units to this unit.</summary>
    public double FromBase(double baseValue) => Conversion.FromBase(baseValue);

    public override string ToString() => $"{Name} ({Symbol})";
}
