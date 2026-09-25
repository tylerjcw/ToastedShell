using System.Drawing;
using System.Text;

namespace Tosh.Stdlib.Plotting;

/// <summary>
/// Terminal canvas implementing high-resolution 2x4 Unicode Braille subpixel rendering,
/// 24-bit ANSI truecolor, and Sixel/Kitty graphics protocols.
/// </summary>
public sealed class TerminalCanvas
{
    public int Columns { get; }
    public int Rows { get; }
    public int SubpixelWidth => Columns * 2;
    public int SubpixelHeight => Rows * 4;

    private readonly byte[,] _dots;
    private readonly Color[,] _colors;

    public TerminalCanvas(int columns = 80, int rows = 24)
    {
        Columns = Math.Max(10, columns);
        Rows = Math.Max(5, rows);
        _dots = new byte[Columns, Rows];
        _colors = new Color[Columns, Rows];
    }

    public void Clear()
    {
        Array.Clear(_dots, 0, _dots.Length);
        Array.Clear(_colors, 0, _colors.Length);
    }

    // Braille subpixel dot masks:
    // (0,0)->0x01  (1,0)->0x08
    // (0,1)->0x02  (1,1)->0x10
    // (0,2)->0x04  (1,2)->0x20
    // (0,3)->0x40  (1,3)->0x80
    private static readonly byte[,] DotMap =
    {
        { 0x01, 0x02, 0x04, 0x40 },
        { 0x08, 0x10, 0x20, 0x80 }
    };

    public void SetPixel(int x, int y, Color color)
    {
        if (x < 0 || x >= SubpixelWidth || y < 0 || y >= SubpixelHeight) return;

        var col = x / 2;
        var row = y / 4;
        var dx = x % 2;
        var dy = y % 4;

        _dots[col, row] |= DotMap[dx, dy];
        _colors[col, row] = color;
    }

    public void DrawLine(int x0, int y0, int x1, int y1, Color color)
    {
        int dx = Math.Abs(x1 - x0);
        int dy = Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;

        while (true)
        {
            SetPixel(x0, y0, color);
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 > -dy)
            {
                err -= dy;
                x0 += sx;
            }
            if (e2 < dx)
            {
                err += dx;
                y0 += sy;
            }
        }
    }

    public void DrawCircle(int cx, int cy, int radius, Color color, bool fill = false)
    {
        if (fill)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (dx * dx + dy * dy <= radius * radius)
                    {
                        SetPixel(cx + dx, cy + dy, color);
                    }
                }
            }
            return;
        }

        int x = radius;
        int y = 0;
        int err = 0;

        while (x >= y)
        {
            SetPixel(cx + x, cy + y, color);
            SetPixel(cx + y, cy + x, color);
            SetPixel(cx - y, cy + x, color);
            SetPixel(cx - x, cy + y, color);
            SetPixel(cx - x, cy - y, color);
            SetPixel(cx - y, cy - x, color);
            SetPixel(cx + y, cy - x, color);
            SetPixel(cx + x, cy - y, color);

            y++;
            if (err <= 0)
            {
                err += 2 * y + 1;
            }
            if (err > 0)
            {
                x--;
                err -= 2 * x + 1;
            }
        }
    }

    public string ToBrailleString()
    {
        var sb = new StringBuilder(Columns * Rows * 8);
        Color? lastColor = null;

        for (int r = 0; r < Rows; r++)
        {
            for (int c = 0; c < Columns; c++)
            {
                var dot = _dots[c, r];
                if (dot == 0)
                {
                    sb.Append(' ');
                }
                else
                {
                    var color = _colors[c, r];
                    if (color != lastColor)
                    {
                        sb.Append(color.ToAnsi());
                        lastColor = color;
                    }
                    sb.Append((char)(0x2800 | dot));
                }
            }
            sb.Append("\x1b[0m\n");
            lastColor = null;
        }

        return sb.ToString();
    }
}
