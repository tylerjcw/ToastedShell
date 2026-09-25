using System.Drawing;
using System.Globalization;
using System.Text;

namespace Tosh.Stdlib.Plotting;

public static class TerminalPlotRenderer
{
    public static string Render(Figure figure, int columns = 80, int rows = 24)
    {
        var sb = new StringBuilder();
        var theme = figure.Theme;

        // Title
        if (!string.IsNullOrEmpty(figure.Title))
        {
            var titlePad = Math.Max(0, (columns - figure.Title.Length) / 2);
            sb.Append(new string(' ', titlePad));
            sb.Append("\x1b[1m");
            sb.Append(theme.TextColor.ToAnsi());
            sb.Append(figure.Title);
            sb.Append("\x1b[0m\n\n");
        }

        var ax = figure.PrimaryAxes;
        var yTicks = ax.YAxis.Scale.GenerateTicks(5);
        var xTicks = ax.XAxis.Scale.GenerateTicks(6);

        // Find longest Y tick label to compute left gutter
        int yGutter = 8;
        foreach (var yt in yTicks)
        {
            var text = ax.YAxis.Scale.FormatTick(yt);
            if (text.Length + 2 > yGutter) yGutter = text.Length + 2;
        }

        int plotCols = Math.Max(20, columns - yGutter - 2);
        int plotRows = Math.Max(10, rows - 6);

        var canvas = new TerminalCanvas(plotCols, plotRows);

        // Configure axis pixel bounds for subpixel coordinates
        ax.XAxis.Scale.PixelMin = 0f;
        ax.XAxis.Scale.PixelMax = canvas.SubpixelWidth - 1;
        ax.YAxis.Scale.PixelMin = canvas.SubpixelHeight - 1; // 0 at bottom
        ax.YAxis.Scale.PixelMax = 0f;                       // max at top

        // Render Series onto terminal canvas
        foreach (var s in ax.Series)
        {
            if (!s.Visible) continue;

            if (s is LineSeries line)
            {
                for (int i = 0; i < line.Points.Count - 1; i++)
                {
                    var p0 = line.Points[i];
                    var p1 = line.Points[i + 1];
                    var x0 = (int)Math.Round(ax.XAxis.Scale.ToPixel(p0.X));
                    var y0 = (int)Math.Round(ax.YAxis.Scale.ToPixel(p0.Y));
                    var x1 = (int)Math.Round(ax.XAxis.Scale.ToPixel(p1.X));
                    var y1 = (int)Math.Round(ax.YAxis.Scale.ToPixel(p1.Y));
                    canvas.DrawLine(x0, y0, x1, y1, line.Color);
                }
            }
            else if (s is ScatterSeries scatter)
            {
                foreach (var (x, y) in scatter.Points)
                {
                    var px = (int)Math.Round(ax.XAxis.Scale.ToPixel(x));
                    var py = (int)Math.Round(ax.YAxis.Scale.ToPixel(y));
                    canvas.DrawCircle(px, py, 1, scatter.Color, fill: true);
                }
            }
            else if (s is BarSeries bar)
            {
                var count = bar.Items.Count;
                var itemW = canvas.SubpixelWidth / (float)Math.Max(1, count);
                var barW = itemW * bar.BarWidth;

                for (int i = 0; i < count; i++)
                {
                    var (_, val) = bar.Items[i];
                    var px = (int)Math.Round(i * itemW + (itemW - barW) / 2f);
                    var py = (int)Math.Round(ax.YAxis.Scale.ToPixel(val));
                    var p0 = (int)Math.Round(ax.YAxis.Scale.ToPixel(0.0));
                    var topY = Math.Min(py, p0);
                    var botY = Math.Max(py, p0);
                    for (int bx = px; bx <= px + (int)barW; bx++)
                    {
                        canvas.DrawLine(bx, topY, bx, botY, bar.Color);
                    }
                }
            }
        }

        // Convert braille canvas into lines with left gutter for Y ticks and axis
        var brailleText = canvas.ToBrailleString();
        var brailleLines = brailleText.Split('\n');

        for (int r = 0; r < plotRows; r++)
        {
            var lineY = ax.YAxis.Scale.ToData(r * 4 + 2);
            // Check if a Y tick lands close to this row
            string tickLabel = "";
            foreach (var yt in yTicks)
            {
                var tickRow = (int)Math.Round(ax.YAxis.Scale.ToPixel(yt) / 4f);
                if (tickRow == r)
                {
                    tickLabel = ax.YAxis.Scale.FormatTick(yt);
                    break;
                }
            }

            var gutter = tickLabel.PadLeft(yGutter - 2);
            sb.Append("\x1b[90m");
            sb.Append(gutter);
            sb.Append(" ┤\x1b[0m");

            if (r < brailleLines.Length)
            {
                sb.Append(brailleLines[r]);
            }
            sb.Append('\n');
        }

        // Bottom X axis spine
        sb.Append(new string(' ', yGutter - 1));
        sb.Append("\x1b[90m└");
        sb.Append(new string('─', plotCols));
        sb.Append("\x1b[0m\n");

        // X tick labels
        var xLabelLine = new char[yGutter + plotCols];
        Array.Fill(xLabelLine, ' ');

        foreach (var xt in xTicks)
        {
            var px = (int)Math.Round(ax.XAxis.Scale.ToPixel(xt) / 2f);
            var label = ax.XAxis.Scale.FormatTick(xt);
            var startPos = yGutter + px - (label.Length / 2);
            if (startPos >= yGutter && startPos + label.Length <= xLabelLine.Length)
            {
                for (int i = 0; i < label.Length; i++)
                {
                    xLabelLine[startPos + i] = label[i];
                }
            }
        }
        sb.Append("\x1b[90m");
        sb.Append(new string(xLabelLine));
        sb.Append("\x1b[0m\n");

        // Legend line if multiple series
        if (ax.Series.Count > 0)
        {
            sb.Append("\n ");
            foreach (var s in ax.Series)
            {
                sb.Append(s.Color.ToAnsi());
                sb.Append("■ ");
                sb.Append("\x1b[0m");
                sb.Append(s.Label);
                sb.Append("   ");
            }
            sb.Append("\n");
        }

        return sb.ToString();
    }

    public static string RenderSixel(Figure figure, int width = 640, int height = 400)
    {
        // High-res Sixel rasterization fallback / protocol
        var sb = new StringBuilder();
        sb.Append($"\x1bPq\"1;1;{width};{height}");
        // Color 0: background
        sb.Append("#0;2;255;255;255");
        // Color 1: primary
        sb.Append("#1;2;31;119;180");
        sb.Append("-");
        sb.Append("\x1b\\");
        return sb.ToString();
    }

    public static string RenderKitty(Figure figure, int width = 640, int height = 400)
    {
        // Kitty graphics protocol wrapper
        return $"\x1b_Gi=1,s={width},v={height},a=T,m=0;\x1b\\";
    }
}
