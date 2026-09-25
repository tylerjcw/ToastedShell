using System.Globalization;

namespace Tosh.Stdlib.Plotting;

public class LinearScale : IScale
{
    public double DataMin { get; set; }
    public double DataMax { get; set; }
    public float PixelMin { get; set; }
    public float PixelMax { get; set; }
    public ITransform1D Transform => IdentityTransform.Instance;

    public LinearScale(double dataMin = 0.0, double dataMax = 1.0, float pixelMin = 0f, float pixelMax = 100f)
    {
        if (double.IsNaN(dataMin) || double.IsInfinity(dataMin)) dataMin = 0.0;
        if (double.IsNaN(dataMax) || double.IsInfinity(dataMax)) dataMax = 1.0;
        if (Math.Abs(dataMax - dataMin) < 1e-12)
        {
            dataMin -= 0.5;
            dataMax += 0.5;
        }

        DataMin = dataMin;
        DataMax = dataMax;
        PixelMin = pixelMin;
        PixelMax = pixelMax;
    }

    public float ToPixel(double data)
    {
        if (double.IsNaN(data)) return float.NaN;
        var span = DataMax - DataMin;
        if (Math.Abs(span) < 1e-12) return (PixelMin + PixelMax) / 2f;
        var fraction = (float)((data - DataMin) / span);
        return PixelMin + fraction * (PixelMax - PixelMin);
    }

    public double ToData(float pixel)
    {
        var pixelSpan = PixelMax - PixelMin;
        if (Math.Abs(pixelSpan) < 1e-6f) return DataMin;
        var fraction = (pixel - PixelMin) / pixelSpan;
        return DataMin + fraction * (DataMax - DataMin);
    }

    public virtual IReadOnlyList<double> GenerateTicks(int targetCount = 5) =>
        TickGenerator.GenerateLinearTicks(DataMin, DataMax, targetCount);

    public virtual string FormatTick(double value) =>
        TickGenerator.FormatNumber(value, Math.Abs(DataMax - DataMin));
}
