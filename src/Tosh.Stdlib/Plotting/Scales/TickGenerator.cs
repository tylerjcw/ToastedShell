using System.Globalization;

namespace Tosh.Stdlib.Plotting;

public static class TickGenerator
{
    public static IReadOnlyList<double> GenerateLinearTicks(double min, double max, int targetCount = 5)
    {
        if (double.IsNaN(min) || double.IsNaN(max) || min >= max)
        {
            return [min];
        }

        var range = NiceNum(max - min, false);
        var step = NiceNum(range / (targetCount <= 1 ? 2 : targetCount - 1), true);
        if (step <= 0 || double.IsInfinity(step)) step = 1.0;

        var start = Math.Ceiling(min / step) * step;
        var end = Math.Floor(max / step) * step;

        var ticks = new List<double>();
        for (double v = start; v <= end + (step * 0.5); v += step)
        {
            // Clean up floating point representation precision artifacts
            var rounded = Math.Round(v, 12);
            ticks.Add(rounded);
        }

        if (ticks.Count == 0)
        {
            ticks.Add(min);
            ticks.Add(max);
        }

        return ticks;
    }

    private static double NiceNum(double range, bool round)
    {
        if (range <= 0 || double.IsNaN(range) || double.IsInfinity(range)) return 1.0;

        var exponent = Math.Floor(Math.Log10(range));
        var fraction = range / Math.Pow(10, exponent);
        double niceFraction;

        if (round)
        {
            if (fraction < 1.5) niceFraction = 1;
            else if (fraction < 3) niceFraction = 2;
            else if (fraction < 7) niceFraction = 5;
            else niceFraction = 10;
        }
        else
        {
            if (fraction <= 1) niceFraction = 1;
            else if (fraction <= 2) niceFraction = 2;
            else if (fraction <= 5) niceFraction = 5;
            else niceFraction = 10;
        }

        return niceFraction * Math.Pow(10, exponent);
    }

    public static string FormatNumber(double val, double rangeSpan)
    {
        if (Math.Abs(val) < 1e-12) return "0";

        if (rangeSpan >= 1e5 || (rangeSpan < 1e-3 && rangeSpan > 0))
        {
            return val.ToString("G4", CultureInfo.InvariantCulture);
        }

        if (rangeSpan >= 10) return val.ToString("F0", CultureInfo.InvariantCulture);
        if (rangeSpan >= 1) return val.ToString("F1", CultureInfo.InvariantCulture);
        if (rangeSpan >= 0.1) return val.ToString("F2", CultureInfo.InvariantCulture);
        if (rangeSpan >= 0.01) return val.ToString("F3", CultureInfo.InvariantCulture);

        return val.ToString("G5", CultureInfo.InvariantCulture);
    }
}
