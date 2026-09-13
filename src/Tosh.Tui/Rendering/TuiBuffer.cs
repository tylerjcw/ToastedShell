namespace Tosh.Tui.Rendering;

/// <summary>
/// A grid of terminal cells: what a screen draws into, instead of building a string.
/// </summary>
/// <remarks>
/// <para>
/// A frame used to be one concatenated string, which meant nothing could be drawn over
/// anything else, the whole screen had to be repainted to change one row, and mouse hit
/// testing needed every screen to remember where it had put things (<c>TUI-0001</c>).
/// A grid fixes all three, because a cell has an address.
/// </para>
/// <para>
/// Writes outside the grid are dropped rather than throwing. Drawing is a rendering
/// path — a widget that miscalculates by a column should draw slightly wrong, not take
/// down the screen a user is looking at.
/// </para>
/// </remarks>
public sealed class TuiBuffer
{
    private readonly TuiCell[] _cells;

    public TuiBuffer(TuiSize size)
    {
        Size = size;
        _cells = new TuiCell[size.Width * size.Height];
        Clear(TuiStyle.Default);
    }

    public TuiSize Size { get; }

    /// <summary>
    /// Where the terminal cursor should sit once this frame is drawn, or
    /// <see langword="null"/> to hide it.
    /// </summary>
    /// <remarks>
    /// Part of the frame rather than something the runtime does around it. The screen
    /// hides the cursor for the whole session today, which is right for a browser and
    /// wrong for a text field: the caret is the one piece of feedback a terminal can
    /// draw better than we can, and only the widget with focus knows where it belongs.
    /// </remarks>
    public (int Column, int Row)? Cursor { get; set; }

    public int Width => Size.Width;

    public int Height => Size.Height;

    /// <summary>The cell at a position, or <see cref="TuiCell.Empty"/> when outside.</summary>
    public TuiCell this[int column, int row]
        => Contains(column, row) ? _cells[(row * Width) + column] : TuiCell.Empty;

    public bool Contains(int column, int row)
        => column >= 0 && column < Width && row >= 0 && row < Height;

    /// <summary>Fills every cell with a blank in the given style.</summary>
    public void Clear(TuiStyle style)
    {
        var blank = new TuiCell(" ", style);

        for (var index = 0; index < _cells.Length; index += 1)
        {
            _cells[index] = blank;
        }
    }

    /// <summary>Fills a region with a blank in the given style.</summary>
    public void Fill(TuiRect region, TuiStyle style)
    {
        for (var row = region.Top; row < region.Bottom; row += 1)
        {
            for (var column = region.Left; column < region.Right; column += 1)
            {
                Set(column, row, new TuiCell(" ", style));
            }
        }
    }

    /// <summary>
    /// Writes one cell, repairing any wide character it partially overwrites.
    /// </summary>
    /// <remarks>
    /// Overwriting half of a two-column character would otherwise leave the other half
    /// on screen with nothing beside it, which a terminal renders as a stray glyph or a
    /// shifted row. Writing over either half blanks the other.
    /// </remarks>
    public void Set(int column, int row, TuiCell cell)
    {
        if (!Contains(column, row))
        {
            return;
        }

        var index = (row * Width) + column;

        // Overwriting the right half of a wide character: blank its left half.
        if (_cells[index].IsContinuation && column > 0)
        {
            _cells[index - 1] = new TuiCell(" ", _cells[index - 1].Style);
        }

        // Overwriting the left half of a wide character: blank its continuation.
        if (column + 1 < Width &&
            _cells[index + 1].IsContinuation &&
            TuiTextMeasure.ClusterWidth(_cells[index].Text) == 2)
        {
            _cells[index + 1] = new TuiCell(" ", _cells[index + 1].Style);
        }

        _cells[index] = cell;
    }

    /// <summary>
    /// Draws text from a position, left to right, and returns the columns consumed.
    /// </summary>
    /// <param name="maxColumns">
    /// A budget in columns. Drawing also stops at the right edge of the buffer.
    /// </param>
    /// <remarks>
    /// A two-column character that would straddle the limit is not drawn at all. Half a
    /// character is not something a cell grid can represent, and a terminal asked to
    /// draw one corrupts the row.
    /// </remarks>
    public int DrawText(int column, int row, string text, TuiStyle style, int? maxColumns = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (!Contains(column, row))
        {
            return 0;
        }

        var limit = Math.Min(Width - column, maxColumns ?? int.MaxValue);
        var used = 0;

        foreach (var cluster in TuiTextMeasure.EnumerateClusters(text))
        {
            var width = TuiTextMeasure.ClusterWidth(cluster);

            if (width == 0)
            {
                // A combining mark belongs to the character before it, which is already
                // in a cell. Appending keeps them together in the same cell.
                if (used > 0)
                {
                    var previous = this[column + used - 1, row];
                    Set(column + used - 1, row, previous with { Text = previous.Text + cluster });
                }

                continue;
            }

            if (used + width > limit)
            {
                break;
            }

            Set(column + used, row, new TuiCell(cluster, style));

            if (width == 2)
            {
                Set(column + used + 1, row, TuiCell.Continuation(style));
            }

            used += width;
        }

        return used;
    }

    /// <summary>Draws <paramref name="source"/> over this buffer at a position.</summary>
    /// <remarks>
    /// This is what makes modals and dropdowns possible: a widget draws into its own
    /// buffer and the result is composited, rather than every screen having to splice
    /// lines into its own output the way <c>ConfigBrowserScreen</c> does today.
    /// </remarks>
    public void Compose(TuiBuffer source, int column, int row)
    {
        ArgumentNullException.ThrowIfNull(source);

        for (var sourceRow = 0; sourceRow < source.Height; sourceRow += 1)
        {
            for (var sourceColumn = 0; sourceColumn < source.Width; sourceColumn += 1)
            {
                Set(column + sourceColumn, row + sourceRow, source[sourceColumn, sourceRow]);
            }
        }
    }

    /// <summary>The plain text of one row, without styling. For tests and diagnostics.</summary>
    public string RowText(int row)
    {
        if (row < 0 || row >= Height)
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder();

        for (var column = 0; column < Width; column += 1)
        {
            builder.Append(this[column, row].Text);
        }

        return builder.ToString();
    }
}
