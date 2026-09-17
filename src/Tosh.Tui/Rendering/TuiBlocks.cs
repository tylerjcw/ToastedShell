namespace Tosh.Tui.Rendering;

/// <summary>
/// The partial-cell characters a chart is drawn out of.
/// </summary>
/// <remarks>
/// A terminal cell is the smallest thing that can be coloured, and a chart wants to say
/// something finer than that. Two families do it: the block elements divide a cell into
/// eighths vertically, and braille divides it into a 2×4 grid of dots. Both were written
/// out where they were used — the eighths twice, under two names — which is how two
/// widgets come to round a partial cell differently.
/// </remarks>
public static class TuiBlocks
{
    /// <summary>Eighths of a cell, from nearly nothing to full height.</summary>
    public static readonly string[] Eighths = ["▁", "▂", "▃", "▄", "▅", "▆", "▇", "█"];

    /// <summary>
    /// The eighth that best represents a fraction of one cell, or null for nothing at all.
    /// </summary>
    /// <remarks>
    /// Rounded away from zero so that a value which is present at all draws something. A
    /// chart where the smallest bar is indistinguishable from no bar is a chart that has
    /// lost the reader's smallest data point.
    /// </remarks>
    public static string? Eighth(double fraction)
    {
        if (fraction <= 0)
        {
            return null;
        }

        var index = (int)Math.Round(Math.Clamp(fraction, 0, 1) * Eighths.Length, MidpointRounding.AwayFromZero);

        return Eighths[Math.Clamp(index - 1, 0, Eighths.Length - 1)];
    }
}

/// <summary>
/// A grid of braille dots, four times the vertical resolution of the cells under it.
/// </summary>
/// <remarks>
/// <para>
/// Each cell carries a 2×4 grid, so a canvas <c>w</c> cells wide and <c>h</c> tall plots
/// <c>2w</c> by <c>4h</c> points. That is the difference between a line chart and a
/// staircase, which is the whole reason for using characters nobody can read.
/// </para>
/// <para>
/// The bit layout is the one the Unicode block was given, and it is not the obvious one:
/// the fourth row of dots was added after the first three, so its two bits sit above the
/// others rather than in sequence.
/// </para>
/// </remarks>
public sealed class TuiBrailleCanvas
{
    // Dots 1,2,3,7 down the left of a cell and 4,5,6,8 down the right.
    private static readonly int[,] Dots =
    {
        { 0x01, 0x08 },
        { 0x02, 0x10 },
        { 0x04, 0x20 },
        { 0x40, 0x80 },
    };

    private readonly int[] _cells;

    public TuiBrailleCanvas(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);

        Width = width;
        Height = height;
        _cells = new int[width * height];
    }

    /// <summary>Width in terminal cells.</summary>
    public int Width { get; }

    /// <summary>Height in terminal cells.</summary>
    public int Height { get; }

    /// <summary>Width in plottable points.</summary>
    public int Columns => Width * 2;

    /// <summary>Height in plottable points.</summary>
    public int Rows => Height * 4;

    /// <summary>
    /// Lights one dot, counting rows from the top.
    /// </summary>
    /// <remarks>
    /// Out-of-range points are dropped rather than clamped. Clamping would pile everything
    /// that overflowed onto the edge row, which draws a solid line along the top of a chart
    /// whose scale is too small and hides the fact that the scale is too small.
    /// </remarks>
    public void Set(int column, int row)
    {
        if (column < 0 || row < 0 || column >= Columns || row >= Rows)
        {
            return;
        }

        _cells[((row / 4) * Width) + (column / 2)] |= Dots[row % 4, column % 2];
    }

    /// <summary>Draws a vertical run of dots, which is how a line joins two samples.</summary>
    /// <remarks>
    /// Without it a series that climbs quickly is a row of disconnected dots: a line chart
    /// is read as a line, and the gap between two samples is the part the eye follows.
    /// </remarks>
    public void SetColumn(int column, int fromRow, int toRow)
    {
        var (start, end) = fromRow <= toRow ? (fromRow, toRow) : (toRow, fromRow);

        for (var row = start; row <= end; row += 1)
        {
            Set(column, row);
        }
    }

    /// <summary>The character for one cell, or a space where no dot is lit.</summary>
    /// <remarks>
    /// A space rather than U+2800 BRAILLE PATTERN BLANK. They look the same and are not:
    /// the blank is a character the folding in <see cref="TuiGlyphs"/> would turn into a
    /// mark, so an empty chart would come out stippled on a terminal without the font.
    /// </remarks>
    public string Cell(int column, int row)
    {
        if (column < 0 || row < 0 || column >= Width || row >= Height)
        {
            return " ";
        }

        var bits = _cells[(row * Width) + column];

        return bits == 0 ? " " : char.ConvertFromUtf32(0x2800 + bits);
    }
}
