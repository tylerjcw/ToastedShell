namespace Tosh.Runtime.Units;

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
        bool isUserDefined = false)
    {
        Symbol = symbol;
        Name = name;
        Category = category;
        Dimension = dimension;
        ToBaseFactor = toBaseFactor;
        ToBaseOffset = toBaseOffset;
        IsUserDefined = isUserDefined;
    }

    /// <summary>Short symbol used in literals and display (e.g. "m", "kg", "mph").</summary>
    public string Symbol { get; }

    /// <summary>Human-readable name (e.g. "meter", "kilogram").</summary>
    public string Name { get; }

    /// <summary>Grouping category (e.g. "Length", "Mass", "Time").</summary>
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

    /// <summary>Whether this unit requires offset conversion (temperature).</summary>
    public bool HasOffset => ToBaseOffset != 0.0;

    /// <summary>Convert a value from this unit to SI base units.</summary>
    public double ToBase(double value) => value * ToBaseFactor + ToBaseOffset;

    /// <summary>Convert a value from SI base units to this unit.</summary>
    public double FromBase(double baseValue) => (baseValue - ToBaseOffset) / ToBaseFactor;

    public override string ToString() => $"{Name} ({Symbol})";
}
