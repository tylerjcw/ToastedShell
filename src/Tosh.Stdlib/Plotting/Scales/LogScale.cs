using System.Globalization;

namespace Tosh.Stdlib.Plotting;

public sealed class LogScale : IScale
{
    public double DataMin { get; set; }
    public double DataMax { get; set; }
    public float PixelMin { get; set; }
    public float PixelMax { get; set; }
    public ITransform1D Transform => Log10Transform.Instance;

    public LogScale(double dataMin = 1.0, double dataMax = 100.0, float pixelMin = 0f, float pixelMax = 100f)
    {
        if (dataMin <= 0.0) dataMin = 1.0;
        if (dataMax <= dataMin) dataMax = dataMin * 10.0;
        DataMin = dataMin;
        DataMax = dataMax;
        PixelMin = pixelMin;
        PixelMax = pixelMax;
    }

    public float ToPixel(double data)
    {
        if (data <= 0.0 || double.IsNaN(data)) return float.NaN;
        var logVal = Math.Log10(data);
        var logMin = Math.Log10(DataMin);
        var logMax = Math.Log10(DataMax);
        var span = logMax - logMin;
        if (Math.Abs(span) < 1e-12) return (PixelMin + PixelMax) / 2f;
        var fraction = (float)((logVal - logMin) / span);
        return PixelMin + fraction * (PixelMax - PixelMin);
    }

    public double ToData(float pixel)
    {
        var pixelSpan = PixelMax - PixelMin;
        if (Math.Abs(pixelSpan) < 1e-6f) return DataMin;
        var fraction = (pixel - PixelMin) / pixelSpan;
        var logMin = Math.Log10(DataMin);
        var logMax = Math.Log10(DataMax);
        var logVal = logMin + fraction * (logMax - logMin);
        return Math.Pow(10.0, logVal);
    }

    public IReadOnlyList<double> GenerateTicks(int targetCount = 5)
    {
        var ticks = new List<double>();
        var minExp = (int)Math.Floor(Math.Log10(DataMin));
        var maxExp = (int)Math.Ceiling(Math.Log10(DataMax));

        for (int exp = minExp; exp <= maxExp; exp++)
        {
            var val = Math.Pow(10.0, exp);
            if (val >= DataMin && val <= DataMax)
            {
                ticks.Add(val);
            }
        }

        if (ticks.Count < 2)
        {
            return TickGenerator.GenerateLinearTicks(DataMin, DataMax, targetCount);
        }

        return ticks;
    }

    public string FormatTick(double value) =>
        value >= 1e4 || (value < 1e-2 && value > 0)
            ? value.ToString("0.##e+0", CultureInfo.InvariantCulture)
            : value.ToString("G", CultureInfo.InvariantCulture);
}
