namespace Tosh.Runtime.Units;

/// <summary>
/// Immutable transform between a displayed unit and its dimension's base unit.
/// Most units are linear; affine units such as Celsius also carry an offset.
/// </summary>
public readonly record struct UnitConversion
{
    public UnitConversion(double toBaseFactor, double toBaseOffset = 0.0)
    {
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

        ToBaseFactor = toBaseFactor;
        ToBaseOffset = toBaseOffset;
    }

    public static UnitConversion Identity { get; } = new(1.0);

    public double ToBaseFactor { get; }

    public double ToBaseOffset { get; }

    public bool IsAffine => ToBaseOffset != 0.0;

    public double ToBase(double value) => value * ToBaseFactor + ToBaseOffset;

    public double FromBase(double baseValue) => (baseValue - ToBaseOffset) / ToBaseFactor;
}
