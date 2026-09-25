using System.Drawing;

namespace Tosh.Stdlib.Plotting;

/// <summary>
/// Top-level Figure containing one or more subplots (Axes).
/// </summary>
public sealed class Figure
{
    public string Title { get; set; } = string.Empty;
    public int Width { get; set; } = 800;
    public int Height { get; set; } = 500;

    public Size Size
    {
        get => new(Width, Height);
        set { Width = value.Width; Height = value.Height; }
    }

    public SizeF SizeF
    {
        get => new(Width, Height);
        set { Width = (int)MathF.Round(value.Width); Height = (int)MathF.Round(value.Height); }
    }

    public PlotTheme Theme { get; set; } = PlotTheme.Light;
    public PlotMargins Margins { get; set; } = PlotMargins.Default;

    public Axes PrimaryAxes { get; }
    public List<Axes> AllAxes { get; } = [];
    public AxesGrid? Grid { get; private set; }

    public Figure(int width = 800, int height = 500, string? title = null, PlotTheme? theme = null)
    {
        Width = width;
        Height = height;
        Title = title ?? string.Empty;
        Theme = theme ?? PlotTheme.Light;
        PrimaryAxes = new Axes();
        AllAxes.Add(PrimaryAxes);
    }

    public Figure(Size size, string? title = null, PlotTheme? theme = null)
        : this(size.Width, size.Height, title, theme) { }

    public Figure(SizeF size, string? title = null, PlotTheme? theme = null)
        : this((int)MathF.Round(size.Width), (int)MathF.Round(size.Height), title, theme) { }

    public static Figure Create(int width = 800, int height = 500, string? title = null, PlotTheme? theme = null) =>
        new(width, height, title, theme);

    public static Figure Create(Size size, string? title = null, PlotTheme? theme = null) =>
        new(size, title, theme);

    public Axes AddAxes(string? title = null)
    {
        var ax = new Axes { Title = title ?? string.Empty };
        AllAxes.Add(ax);
        return ax;
    }

    public AxesGrid Subplots(int rows, int cols)
    {
        Grid = new AxesGrid(rows, cols);
        AllAxes.Clear();
        for (int r = 0; r < Grid.Rows; r++)
        {
            for (int c = 0; c < Grid.Columns; c++)
            {
                AllAxes.Add(Grid.At(r, c));
            }
        }
        return Grid;
    }

    // Convenience methods mapping to PrimaryAxes
    public LineSeries Plot(IEnumerable<double> y, string? label = null, Color? color = null) =>
        PrimaryAxes.Plot(y, label, color);

    public LineSeries Plot(IEnumerable<double> x, IEnumerable<double> y, string? label = null, Color? color = null) =>
        PrimaryAxes.Plot(x, y, label, color);

    public LineSeries Plot(IEnumerable<PointF> points, string? label = null, Color? color = null) =>
        PrimaryAxes.Plot(points, label, color);

    public LineSeries Plot(IEnumerable<Point> points, string? label = null, Color? color = null) =>
        PrimaryAxes.Plot(points, label, color);

    public ScatterSeries Scatter(IEnumerable<double> x, IEnumerable<double> y, string? label = null, Color? color = null, float size = 5f) =>
        PrimaryAxes.Scatter(x, y, label, color, size);

    public ScatterSeries Scatter(IEnumerable<PointF> points, string? label = null, Color? color = null, float size = 5f) =>
        PrimaryAxes.Scatter(points, label, color, size);

    public ScatterSeries Scatter(IEnumerable<Point> points, string? label = null, Color? color = null, float size = 5f) =>
        PrimaryAxes.Scatter(points, label, color, size);

    public BarSeries Bar(IEnumerable<string> categories, IEnumerable<double> values, string? label = null, Color? color = null) =>
        PrimaryAxes.Bar(categories, values, label, color);

    public BandSeries FillBetween(IEnumerable<double> x, IEnumerable<double> lower, IEnumerable<double> upper, string? label = null, Color? color = null) =>
        PrimaryAxes.FillBetween(x, lower, upper, label, color);

    // Rendering delegates
    public string ToSvg() => SvgPlotRenderer.Render(this);

    public void SaveSvg(string filePath)
    {
        var svg = ToSvg();
        File.WriteAllText(filePath, svg);
    }

    public void SaveHtml(string filePath)
    {
        var svg = ToSvg();
        var safeTitle = System.Security.SecurityElement.Escape(Title.Length > 0 ? Title : "TōSh Plot");
        var html = "<!DOCTYPE html>\n<html>\n<head>\n<meta charset='utf-8'/>\n<title>" + safeTitle + "</title>\n" +
                   "<style>body { margin: 0; padding: 24px; background: #111; color: #eee; font-family: system-ui, sans-serif; display: flex; justify-content: center; } .plot-box { background: white; border-radius: 8px; padding: 8px; box-shadow: 0 4px 20px rgba(0,0,0,0.5); }</style>\n" +
                   "</head>\n<body>\n<div class='plot-box'>\n" + svg + "\n</div>\n</body>\n</html>";
        File.WriteAllText(filePath, html);
    }

    public string ToTerminalString(int terminalWidth = 80, int terminalHeight = 24) =>
        TerminalPlotRenderer.Render(this, terminalWidth, terminalHeight);

    public string ToSixelString(int terminalWidth = 640, int terminalHeight = 400) =>
        TerminalPlotRenderer.RenderSixel(this, terminalWidth, terminalHeight);

    public string ToKittyString(int terminalWidth = 640, int terminalHeight = 400) =>
        TerminalPlotRenderer.RenderKitty(this, terminalWidth, terminalHeight);

    public override string ToString() => ToTerminalString();
}
