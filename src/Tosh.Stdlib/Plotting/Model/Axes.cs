using System.Drawing;

namespace Tosh.Stdlib.Plotting;

public sealed class Axes
{
    public string Title { get; set; } = string.Empty;
    public Axis XAxis { get; } = new();
    public Axis YAxis { get; } = new();
    public PlotLegend Legend { get; } = new();
    public List<PlotSeries> Series { get; } = [];
    public bool ShowGrid { get; set; } = true;
    public bool ShowMinorGrid { get; set; } = false;
    public RectangleF? Bounds { get; set; }

    private int _colorIndex = 0;

    public LineSeries Plot(IEnumerable<double> y, string? label = null, Color? color = null) =>
        Plot(Enumerable.Range(0, y.Count()).Select(i => (double)i), y, label, color);

    public LineSeries Plot(IEnumerable<double> x, IEnumerable<double> y, string? label = null, Color? color = null)
    {
        var points = x.Zip(y, (px, py) => (px, py)).ToList();
        var seriesColor = color ?? NextColor();
        var series = new LineSeries
        {
            Points = points,
            Label = label ?? $"Series {Series.Count + 1}",
            Color = seriesColor
        };
        Series.Add(series);
        AutoScale();
        return series;
    }

    public LineSeries Plot(IEnumerable<PointF> points, string? label = null, Color? color = null)
    {
        var ptList = points.Select(p => ((double)p.X, (double)p.Y)).ToList();
        var seriesColor = color ?? NextColor();
        var series = new LineSeries
        {
            Points = ptList,
            Label = label ?? $"Series {Series.Count + 1}",
            Color = seriesColor
        };
        Series.Add(series);
        AutoScale();
        return series;
    }

    public LineSeries Plot(IEnumerable<Point> points, string? label = null, Color? color = null) =>
        Plot(points.Select(p => new PointF(p.X, p.Y)), label, color);

    public ScatterSeries Scatter(IEnumerable<double> x, IEnumerable<double> y, string? label = null, Color? color = null, float size = 5f)
    {
        var points = x.Zip(y, (px, py) => (px, py)).ToList();
        var seriesColor = color ?? NextColor();
        var series = new ScatterSeries
        {
            Points = points,
            Label = label ?? $"Scatter {Series.Count + 1}",
            Color = seriesColor,
            MarkerSize = size
        };
        Series.Add(series);
        AutoScale();
        return series;
    }

    public ScatterSeries Scatter(IEnumerable<PointF> points, string? label = null, Color? color = null, float size = 5f)
    {
        var ptList = points.Select(p => ((double)p.X, (double)p.Y)).ToList();
        var seriesColor = color ?? NextColor();
        var series = new ScatterSeries
        {
            Points = ptList,
            Label = label ?? $"Scatter {Series.Count + 1}",
            Color = seriesColor,
            MarkerSize = size
        };
        Series.Add(series);
        AutoScale();
        return series;
    }

    public ScatterSeries Scatter(IEnumerable<Point> points, string? label = null, Color? color = null, float size = 5f) =>
        Scatter(points.Select(p => new PointF(p.X, p.Y)), label, color, size);

    public BarSeries Bar(IEnumerable<string> categories, IEnumerable<double> values, string? label = null, Color? color = null)
    {
        var items = categories.Zip(values, (cat, val) => (cat, val)).ToList();
        var seriesColor = color ?? NextColor();
        var series = new BarSeries
        {
            Items = items,
            Label = label ?? $"Bar {Series.Count + 1}",
            Color = seriesColor
        };
        Series.Add(series);
        AutoScale();
        return series;
    }

    public BandSeries FillBetween(IEnumerable<double> x, IEnumerable<double> lower, IEnumerable<double> upper, string? label = null, Color? color = null)
    {
        var xList = x.ToList();
        var lowerList = lower.ToList();
        var upperList = upper.ToList();
        var count = Math.Min(xList.Count, Math.Min(lowerList.Count, upperList.Count));
        var points = new List<(double X, double Lower, double Upper)>(count);
        for (int i = 0; i < count; i++)
        {
            points.Add((xList[i], lowerList[i], upperList[i]));
        }

        var seriesColor = color ?? NextColor();
        var series = new BandSeries
        {
            BandPoints = points,
            Label = label ?? $"Band {Series.Count + 1}",
            Color = seriesColor
        };
        Series.Add(series);
        AutoScale();
        return series;
    }

    public void AutoScale()
    {
        if (Series.Count == 0) return;

        double overallMinX = double.MaxValue, overallMaxX = double.MinValue;
        double overallMinY = double.MaxValue, overallMaxY = double.MinValue;

        foreach (var s in Series)
        {
            if (!s.Visible) continue;
            var (minX, maxX, minY, maxY) = s.GetBounds();
            if (minX < overallMinX) overallMinX = minX;
            if (maxX > overallMaxX) overallMaxX = maxX;
            if (minY < overallMinY) overallMinY = minY;
            if (maxY > overallMaxY) overallMaxY = maxY;
        }

        if (overallMinX <= overallMaxX) XAxis.AutoScale(overallMinX, overallMaxX);
        if (overallMinY <= overallMaxY) YAxis.AutoScale(overallMinY, overallMaxY);
    }

    private Color NextColor()
    {
        var palette = PlotColorExtensions.DefaultPalette;
        var color = palette[_colorIndex % palette.Length];
        _colorIndex++;
        return color;
    }
}
