using System.Drawing;

namespace Tosh.Stdlib.Plotting;

public static class SvgPlotRenderer
{
    public static string Render(Figure figure)
    {
        var canvas = new SvgCanvas(figure.Width, figure.Height, figure.Title);
        var theme = figure.Theme;

        // Background
        canvas.Rect(new RectangleF(0, 0, figure.Width, figure.Height), theme.BackgroundColor);

        // Figure title
        if (!string.IsNullOrEmpty(figure.Title))
        {
            canvas.Text(new PointF(figure.Width / 2f, 25f), figure.Title, 16f, "middle", theme.TextColor, bold: true);
        }

        if (figure.Grid is not null)
        {
            RenderGrid(canvas, figure, figure.Grid, theme);
        }
        else
        {
            var topOffset = string.IsNullOrEmpty(figure.Title) ? figure.Margins.Top : figure.Margins.Top + 15f;
            var plotRect = new RectangleF(
                figure.Margins.Left,
                topOffset,
                figure.Width - figure.Margins.Left - figure.Margins.Right,
                figure.Height - topOffset - figure.Margins.Bottom);

            RenderAxes(canvas, figure.PrimaryAxes, plotRect, theme);
        }

        return canvas.Finish();
    }

    private static void RenderGrid(SvgCanvas canvas, Figure figure, AxesGrid grid, PlotTheme theme)
    {
        var topOffset = string.IsNullOrEmpty(figure.Title) ? figure.Margins.Top : figure.Margins.Top + 15f;
        var totalWidth = figure.Width - figure.Margins.Left - figure.Margins.Right;
        var totalHeight = figure.Height - topOffset - figure.Margins.Bottom;

        var cellW = totalWidth / grid.Columns;
        var cellH = totalHeight / grid.Rows;
        var gap = 20f;

        for (int r = 0; r < grid.Rows; r++)
        {
            for (int c = 0; c < grid.Columns; c++)
            {
                var ax = grid.At(r, c);
                var cellRect = new RectangleF(
                    figure.Margins.Left + (c * cellW) + (gap * 0.5f),
                    topOffset + (r * cellH) + (gap * 0.5f),
                    cellW - gap,
                    cellH - gap);

                RenderAxes(canvas, ax, cellRect, theme);
            }
        }
    }

    public static void RenderAxes(SvgCanvas canvas, Axes ax, RectangleF plotRect, PlotTheme theme)
    {
        // Plot canvas area
        canvas.Rect(plotRect, theme.CanvasColor);

        // Configure scales with pixel bounds
        ax.XAxis.Scale.PixelMin = plotRect.Left;
        ax.XAxis.Scale.PixelMax = plotRect.Right;
        ax.YAxis.Scale.PixelMin = plotRect.Bottom; // Data min is at bottom
        ax.YAxis.Scale.PixelMax = plotRect.Top;    // Data max is at top

        // Ticks
        var xTicks = ax.XAxis.Scale.GenerateTicks(6);
        var yTicks = ax.YAxis.Scale.GenerateTicks(5);

        // Grid lines
        if (ax.ShowGrid)
        {
            foreach (var xt in xTicks)
            {
                var px = ax.XAxis.Scale.ToPixel(xt);
                if (px >= plotRect.Left && px <= plotRect.Right)
                {
                    canvas.Line(new PointF(px, plotRect.Top), new PointF(px, plotRect.Bottom), theme.GridColor, 1f);
                }
            }

            foreach (var yt in yTicks)
            {
                var py = ax.YAxis.Scale.ToPixel(yt);
                if (py >= plotRect.Top && py <= plotRect.Bottom)
                {
                    canvas.Line(new PointF(plotRect.Left, py), new PointF(plotRect.Right, py), theme.GridColor, 1f);
                }
            }
        }

        // Axes Spines
        canvas.Line(new PointF(plotRect.Left, plotRect.Bottom), new PointF(plotRect.Right, plotRect.Bottom), theme.AxisColor, 1.5f);
        canvas.Line(new PointF(plotRect.Left, plotRect.Top), new PointF(plotRect.Left, plotRect.Bottom), theme.AxisColor, 1.5f);

        // Tick marks & labels
        foreach (var xt in xTicks)
        {
            var px = ax.XAxis.Scale.ToPixel(xt);
            if (px >= plotRect.Left - 1 && px <= plotRect.Right + 1)
            {
                canvas.Line(new PointF(px, plotRect.Bottom), new PointF(px, plotRect.Bottom + 5f), theme.AxisColor, 1f);
                var label = ax.XAxis.Scale.FormatTick(xt);
                canvas.Text(new PointF(px, plotRect.Bottom + 18f), label, 11f, "middle", theme.TextColor);
            }
        }

        foreach (var yt in yTicks)
        {
            var py = ax.YAxis.Scale.ToPixel(yt);
            if (py >= plotRect.Top - 1 && py <= plotRect.Bottom + 1)
            {
                canvas.Line(new PointF(plotRect.Left - 5f, py), new PointF(plotRect.Left, py), theme.AxisColor, 1f);
                var label = ax.YAxis.Scale.FormatTick(yt);
                canvas.Text(new PointF(plotRect.Left - 8f, py + 4f), label, 11f, "end", theme.TextColor);
            }
        }

        // Axis Titles
        if (!string.IsNullOrEmpty(ax.XAxis.Label))
        {
            canvas.Text(new PointF(plotRect.Left + (plotRect.Width / 2f), plotRect.Bottom + 35f), ax.XAxis.Label, 12f, "middle", theme.TextColor, bold: true);
        }

        if (!string.IsNullOrEmpty(ax.YAxis.Label))
        {
            canvas.Text(new PointF(plotRect.Left - 40f, plotRect.Top + (plotRect.Height / 2f)), ax.YAxis.Label, 12f, "middle", theme.TextColor, rotation: -90f, bold: true);
        }

        if (!string.IsNullOrEmpty(ax.Title))
        {
            canvas.Text(new PointF(plotRect.Left + (plotRect.Width / 2f), plotRect.Top - 8f), ax.Title, 13f, "middle", theme.TextColor, bold: true);
        }

        // Clip series to plot rectangle
        canvas.BeginClip(plotRect);

        foreach (var s in ax.Series)
        {
            if (!s.Visible) continue;

            if (s is BandSeries band)
            {
                var polygonPoints = new List<PointF>();
                for (int i = 0; i < band.BandPoints.Count; i++)
                {
                    var (x, _, upper) = band.BandPoints[i];
                    polygonPoints.Add(new PointF(ax.XAxis.Scale.ToPixel(x), ax.YAxis.Scale.ToPixel(upper)));
                }
                for (int i = band.BandPoints.Count - 1; i >= 0; i--)
                {
                    var (x, lower, _) = band.BandPoints[i];
                    polygonPoints.Add(new PointF(ax.XAxis.Scale.ToPixel(x), ax.YAxis.Scale.ToPixel(lower)));
                }
                var fillColor = Color.FromArgb((byte)(band.Opacity * 255), band.Color.R, band.Color.G, band.Color.B);
                canvas.Polygon(polygonPoints, fillColor);
            }
            else if (s is BarSeries bar)
            {
                var count = bar.Items.Count;
                var totalW = plotRect.Width;
                var itemW = totalW / Math.Max(1, count);
                var actualBarW = itemW * bar.BarWidth;

                for (int i = 0; i < count; i++)
                {
                    var (_, val) = bar.Items[i];
                    var px = plotRect.Left + (i * itemW) + ((itemW - actualBarW) / 2f);
                    var py = ax.YAxis.Scale.ToPixel(val);
                    var p0 = ax.YAxis.Scale.ToPixel(0.0);
                    var barTop = Math.Min(py, p0);
                    var barH = Math.Abs(py - p0);
                    canvas.Rect(new RectangleF(px, barTop, actualBarW, barH), bar.Color);
                }
            }
            else if (s is LineSeries line)
            {
                var pts = line.Points.Select(p => new PointF(ax.XAxis.Scale.ToPixel(p.X), ax.YAxis.Scale.ToPixel(p.Y))).ToList();
                canvas.Path(pts, line.Color, line.LineWidth, line.StrokeDash);

                if (line.ShowMarkers)
                {
                    foreach (var pt in pts)
                    {
                        canvas.Circle(pt, line.MarkerSize, line.Color);
                    }
                }
            }
            else if (s is ScatterSeries scatter)
            {
                foreach (var (x, y) in scatter.Points)
                {
                    var pt = new PointF(ax.XAxis.Scale.ToPixel(x), ax.YAxis.Scale.ToPixel(y));
                    canvas.Circle(pt, scatter.MarkerSize, scatter.Color, scatter.BorderColor);
                }
            }
        }

        canvas.EndClip();

        // Legend
        if (ax.Legend.Visible && ax.Series.Count > 0)
        {
            var legendX = plotRect.Right - 130f;
            var legendY = plotRect.Top + 10f;
            var legendW = 120f;
            var legendH = 15f + (ax.Series.Count * 18f);

            canvas.Rect(new RectangleF(legendX, legendY, legendW, legendH), Color.FromArgb(230, theme.CanvasColor.R, theme.CanvasColor.G, theme.CanvasColor.B), theme.GridColor);

            for (int i = 0; i < ax.Series.Count; i++)
            {
                var s = ax.Series[i];
                var entryY = legendY + 12f + (i * 18f);
                canvas.Line(new PointF(legendX + 10f, entryY), new PointF(legendX + 30f, entryY), s.Color, 2f);
                canvas.Circle(new PointF(legendX + 20f, entryY), 3f, s.Color);
                canvas.Text(new PointF(legendX + 38f, entryY + 4f), s.Label, 10f, "start", theme.TextColor);
            }
        }
    }
}
