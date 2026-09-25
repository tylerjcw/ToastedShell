namespace Tosh.Stdlib.Plotting;

/// <summary>
/// 1D continuous monotonic transformation function.
/// </summary>
public interface ITransform1D
{
    double Forward(double value);
    double Inverse(double value);
}

public sealed class IdentityTransform : ITransform1D
{
    public static readonly IdentityTransform Instance = new();
    public double Forward(double value) => value;
    public double Inverse(double value) => value;
}

public sealed class Log10Transform : ITransform1D
{
    public static readonly Log10Transform Instance = new();
    public double Forward(double value) => value <= 0.0 ? double.NaN : Math.Log10(value);
    public double Inverse(double value) => Math.Pow(10.0, value);
}
