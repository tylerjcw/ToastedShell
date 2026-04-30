namespace Tosh.Runtime.Units;

// ── Named Quantity types ──────────────────────────────────────────────
// Each wraps Quantity with a fixed category name and proper display.
// For Duration and DataSize, interop properties bridge to TimeSpan/StorageSize.

public class LengthQuantity(double magnitude, string unitSymbol)
    : Quantity(magnitude, UnitExpression.Of(UnitDimension.Length), unitSymbol)
{
    public override string CategoryName => "Length";
}

public class MassQuantity(double magnitude, string unitSymbol)
    : Quantity(magnitude, UnitExpression.Of(UnitDimension.Mass), unitSymbol)
{
    public override string CategoryName => "Mass";
}

public class DurationQuantity : Quantity
{
    private static readonly UnitExpression TimeDimension = UnitExpression.Of(UnitDimension.Time);

    public DurationQuantity(double magnitude, string unitSymbol)
        : base(magnitude, TimeDimension, unitSymbol) { }

    public override string CategoryName => "Duration";

    /// <summary>Convert to a .NET TimeSpan (base value is in seconds).</summary>
    public TimeSpan TimeSpan => TimeSpan.FromSeconds(BaseValue);

    public double TotalSeconds => BaseValue;
    public double TotalMinutes => BaseValue / 60.0;
    public double TotalHours => BaseValue / 3600.0;
    public double TotalDays => BaseValue / 86400.0;
    public double TotalMilliseconds => BaseValue * 1000.0;
}

public class TemperatureQuantity(double magnitude, string unitSymbol)
    : Quantity(magnitude, UnitExpression.Of(UnitDimension.Temperature), unitSymbol)
{
    public override string CategoryName => "Temperature";
}

public class DataSizeQuantity : Quantity
{
    private static readonly UnitExpression DataDimension = UnitExpression.Of(UnitDimension.Data);

    public DataSizeQuantity(double magnitude, string unitSymbol)
        : base(magnitude, DataDimension, unitSymbol) { }

    public override string CategoryName => "DataSize";

    /// <summary>Convert to a StorageSize (base value is in bits, StorageSize wants bytes).</summary>
    public StorageSize StorageSize => StorageSize.FromBytes((long)(BaseValue / 8.0));

    public double TotalBits => BaseValue;
    public double TotalBytes => BaseValue / 8.0;
}

public class SpeedQuantity(double magnitude, string unitSymbol)
    : Quantity(magnitude, UnitExpression.Of((UnitDimension.Length, 1), (UnitDimension.Time, -1)), unitSymbol)
{
    public override string CategoryName => "Speed";
}

public class AreaQuantity(double magnitude, string unitSymbol)
    : Quantity(magnitude, UnitExpression.Of(UnitDimension.Length, 2), unitSymbol)
{
    public override string CategoryName => "Area";
}

public class VolumeQuantity(double magnitude, string unitSymbol)
    : Quantity(magnitude, UnitExpression.Of(UnitDimension.Length, 3), unitSymbol)
{
    public override string CategoryName => "Volume";
}

public class ForceQuantity(double magnitude, string unitSymbol)
    : Quantity(magnitude, UnitExpression.Of((UnitDimension.Mass, 1), (UnitDimension.Length, 1), (UnitDimension.Time, -2)), unitSymbol)
{
    public override string CategoryName => "Force";
}

public class EnergyQuantity(double magnitude, string unitSymbol)
    : Quantity(magnitude, UnitExpression.Of((UnitDimension.Mass, 1), (UnitDimension.Length, 2), (UnitDimension.Time, -2)), unitSymbol)
{
    public override string CategoryName => "Energy";
}

public class PowerQuantity(double magnitude, string unitSymbol)
    : Quantity(magnitude, UnitExpression.Of((UnitDimension.Mass, 1), (UnitDimension.Length, 2), (UnitDimension.Time, -3)), unitSymbol)
{
    public override string CategoryName => "Power";
}

public class PressureQuantity(double magnitude, string unitSymbol)
    : Quantity(magnitude, UnitExpression.Of((UnitDimension.Mass, 1), (UnitDimension.Length, -1), (UnitDimension.Time, -2)), unitSymbol)
{
    public override string CategoryName => "Pressure";
}

public class FrequencyQuantity(double magnitude, string unitSymbol)
    : Quantity(magnitude, UnitExpression.Of(UnitDimension.Time, -1), unitSymbol)
{
    public override string CategoryName => "Frequency";
}

public class AngleQuantity(double magnitude, string unitSymbol)
    : Quantity(magnitude, UnitExpression.Of(UnitDimension.Angle), unitSymbol)
{
    public override string CategoryName => "Angle";
}

public class AccelerationQuantity(double magnitude, string unitSymbol)
    : Quantity(magnitude, UnitExpression.Of((UnitDimension.Length, 1), (UnitDimension.Time, -2)), unitSymbol)
{
    public override string CategoryName => "Acceleration";
}

public class DensityQuantity(double magnitude, string unitSymbol)
    : Quantity(magnitude, UnitExpression.Of((UnitDimension.Mass, 1), (UnitDimension.Length, -3)), unitSymbol)
{
    public override string CategoryName => "Density";
}

public class VoltageQuantity(double magnitude, string unitSymbol)
    : Quantity(magnitude, UnitExpression.Of((UnitDimension.Mass, 1), (UnitDimension.Length, 2), (UnitDimension.Time, -3), (UnitDimension.ElectricCurrent, -1)), unitSymbol)
{
    public override string CategoryName => "Voltage";
}

public class CurrentQuantity(double magnitude, string unitSymbol)
    : Quantity(magnitude, UnitExpression.Of(UnitDimension.ElectricCurrent), unitSymbol)
{
    public override string CategoryName => "Current";
}

public class ResistanceQuantity(double magnitude, string unitSymbol)
    : Quantity(magnitude, UnitExpression.Of((UnitDimension.Mass, 1), (UnitDimension.Length, 2), (UnitDimension.Time, -3), (UnitDimension.ElectricCurrent, -2)), unitSymbol)
{
    public override string CategoryName => "Resistance";
}

public class ChargeQuantity(double magnitude, string unitSymbol)
    : Quantity(magnitude, UnitExpression.Of((UnitDimension.ElectricCurrent, 1), (UnitDimension.Time, 1)), unitSymbol)
{
    public override string CategoryName => "Charge";
}

public class TorqueQuantity(double magnitude, string unitSymbol)
    : Quantity(magnitude, UnitExpression.Of((UnitDimension.Mass, 1), (UnitDimension.Length, 2), (UnitDimension.Time, -2)), unitSymbol)
{
    public override string CategoryName => "Torque";
}

public class FlowRateQuantity(double magnitude, string unitSymbol)
    : Quantity(magnitude, UnitExpression.Of((UnitDimension.Length, 3), (UnitDimension.Time, -1)), unitSymbol)
{
    public override string CategoryName => "FlowRate";
}
