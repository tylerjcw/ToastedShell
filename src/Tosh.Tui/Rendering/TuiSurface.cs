namespace Tosh.Tui.Rendering;

/// <summary>
/// A widget's view of the screen: a region of a <see cref="TuiBuffer"/> addressed from
/// its own top-left corner, which it cannot draw outside of.
/// </summary>
/// <remarks>
/// <para>
/// Widgets should not know where they are. A list that draws at absolute coordinates has
/// to be told its offset, which means every container has to pass one down and every
/// widget has to add it to every write — and one forgotten addition draws over a
/// neighbour. A surface makes position the container's business: the child draws at
/// <c>(0, 0)</c> and lands wherever it was placed.
/// </para>
/// <para>
/// Clipping is enforced rather than trusted. Writes outside the region are dropped, so a
/// widget that miscalculates its own size spoils its own box instead of corrupting the
/// screen around it.
/// </para>
/// </remarks>
public readonly struct TuiSurface
{
    private readonly TuiBuffer _buffer;

    public TuiSurface(TuiBuffer buffer, TuiRect bounds)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        _buffer = buffer;
        Bounds = bounds.Intersect(new TuiRect(0, 0, buffer.Width, buffer.Height));
    }

    /// <summary>A surface covering a whole buffer.</summary>
    public TuiSurface(TuiBuffer buffer)
        : this(buffer, new TuiRect(0, 0, buffer?.Width ?? 0, buffer?.Height ?? 0))
    {
    }

    /// <summary>Where this surface sits in the underlying buffer.</summary>
    public TuiRect Bounds { get; }

    public int Width => Bounds.Width;

    public int Height => Bounds.Height;

    public TuiSize Size => new(Bounds.Width, Bounds.Height);

    /// <summary>A sub-region, addressed relative to this surface.</summary>
    public TuiSurface Clip(TuiRect region)
        => new(_buffer, region.Offset(Bounds.Left, Bounds.Top).Intersect(Bounds));

    /// <summary>Writes one cell at a position relative to this surface.</summary>
    public void Set(int column, int row, TuiCell cell)
    {
        if (column < 0 || row < 0 || column >= Width || row >= Height)
        {
            return;
        }

        _buffer.Set(Bounds.Left + column, Bounds.Top + row, cell);
    }

    /// <summary>Reads one cell at a position relative to this surface.</summary>
    public TuiCell Get(int column, int row)
        => column < 0 || row < 0 || column >= Width || row >= Height
            ? TuiCell.Empty
            : _buffer[Bounds.Left + column, Bounds.Top + row];

    /// <summary>Draws text from a position relative to this surface.</summary>
    public int DrawText(int column, int row, string text, TuiStyle style, int? maxColumns = null)
    {
        if (row < 0 || row >= Height || column >= Width)
        {
            return 0;
        }

        var available = Width - Math.Max(0, column);
        var limit = Math.Min(available, maxColumns ?? int.MaxValue);

        return _buffer.DrawText(Bounds.Left + column, Bounds.Top + row, text, style, limit);
    }

    /// <summary>Fills this whole surface with a blank in the given style.</summary>
    public void Fill(TuiStyle style) => _buffer.Fill(Bounds, style);
}
