using System.Drawing;

namespace Tosh.Stdlib.Plotting;

public abstract class PlotSeries
{
    public string Label { get; set; } = string.Empty;
    public Color Color { get; set; } = Color.FromArgb(0x1F, 0x77, 0xB4);
    public bool Visible { get; set; } = true;

    public abstract (double MinX, double MaxX, double MinY, double MaxY) GetBounds();
}

public sealed class LineSeries : PlotSeries
{
    public IReadOnlyList<(double X, double Y)> Points { get; set; } = Array.Empty<(double, double)>();
    public float LineWidth { get; set; } = 2.0f;
    public bool ShowMarkers { get; set; } = false;
    public float MarkerSize { get; set; } = 4.0f;
    public string StrokeDash { get; set; } = string.Empty;

    public override (double MinX, double MaxX, double MinY, double MaxY) GetBounds()
    {
        if (Points.Count == 0) return (0, 1, 0, 1);
        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;

        foreach (var (x, y) in Points)
        {
            if (double.IsNaN(x) || double.IsNaN(y)) continue;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        return (minX == double.MaxValue ? 0 : minX,
                maxX == double.MinValue ? 1 : maxX,
                minY == double.MaxValue ? 0 : minY,
                maxY == double.MinValue ? 1 : maxY);
    }
}

public sealed class ScatterSeries : PlotSeries
{
    public IReadOnlyList<(double X, double Y)> Points { get; set; } = Array.Empty<(double, double)>();
    public float MarkerSize { get; set; } = 5.0f;
    public string MarkerShape { get; set; } = "circle";
    public Color? BorderColor { get; set; }

    public override (double MinX, double MaxX, double MinY, double MaxY) GetBounds()
    {
        if (Points.Count == 0) return (0, 1, 0, 1);
        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;

        foreach (var (x, y) in Points)
        {
            if (double.IsNaN(x) || double.IsNaN(y)) continue;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        return (minX == double.MaxValue ? 0 : minX,
                maxX == double.MinValue ? 1 : maxX,
                minY == double.MaxValue ? 0 : minY,
                maxY == double.MinValue ? 1 : maxY);
    }
}

public sealed class BarSeries : PlotSeries
{
    public IReadOnlyList<(string Category, double Value)> Items { get; set; } = Array.Empty<(string, double)>();
    public float BarWidth { get; set; } = 0.7f;

    public override (double MinX, double MaxX, double MinY, double MaxY) GetBounds()
    {
        if (Items.Count == 0) return (0, 1, 0, 1);
        double minY = 0.0;
        double maxY = double.MinValue;

        foreach (var (_, val) in Items)
        {
            if (double.IsNaN(val)) continue;
            if (val > maxY) maxY = val;
            if (val < minY) minY = val;
        }

        return (-0.5, Items.Count - 0.5, minY, maxY == double.MinValue ? 1.0 : maxY);
    }
}

public sealed class BandSeries : PlotSeries
{
    public IReadOnlyList<(double X, double Lower, double Upper)> BandPoints { get; set; } = Array.Empty<(double, double, double)>();
    public float Opacity { get; set; } = 0.35f;

    public override (double MinX, double MaxX, double MinY, double MaxY) GetBounds()
    {
        if (BandPoints.Count == 0) return (0, 1, 0, 1);
        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;

        foreach (var (x, lower, upper) in BandPoints)
        {
            if (double.IsNaN(x)) continue;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (!double.IsNaN(lower) && lower < minY) minY = lower;
            if (!double.IsNaN(upper) && upper > maxY) maxY = upper;
        }

        return (minX == double.MaxValue ? 0 : minX,
                maxX == double.MinValue ? 1 : maxX,
                minY == double.MaxValue ? 0 : minY,
                maxY == double.MinValue ? 1 : maxY);
    }
}
