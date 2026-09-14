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

    /// <summary>
    /// Asks the terminal to draw a picture over part of this surface.
    /// </summary>
    /// <remarks>
    /// The rectangle is in surface coordinates like everything else a widget says, and is
    /// clipped to what the surface can actually show — so a picture in a pane scrolled
    /// half off the screen asks for half a picture rather than for a picture the terminal
    /// would happily draw over the pane beside it.
    /// </remarks>
    /// <returns>The part of the rectangle that was placed, empty when none of it was.</returns>
    public TuiRect Place(int id, TuiRect region, TuiPixels pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);

        var wanted = new TuiRect(
            Bounds.Left + region.Left,
            Bounds.Top + region.Top,
            region.Width,
            region.Height);

        var visible = wanted.Intersect(Bounds);

        if (visible.IsEmpty || pixels.IsEmpty)
        {
            return default;
        }

        _buffer.Place(new TuiPlacement(
            id,
            visible.Left,
            visible.Top,
            visible.Width,
            visible.Height,
            visible == wanted ? pixels : Crop(pixels, wanted, visible)));

        return new TuiRect(visible.Left - Bounds.Left, visible.Top - Bounds.Top, visible.Width, visible.Height);
    }

    /// <summary>The part of a picture that a clipped placement should carry.</summary>
    private static TuiPixels Crop(TuiPixels pixels, TuiRect wanted, TuiRect visible)
    {
        // Proportional, because the picture was already scaled to `wanted` when the widget
        // decided how big to ask for it: the cells that survive keep the pixels they had.
        var left = (visible.Left - wanted.Left) * pixels.Width / wanted.Width;
        var top = (visible.Top - wanted.Top) * pixels.Height / wanted.Height;
        var width = Math.Max(1, visible.Width * pixels.Width / wanted.Width);
        var height = Math.Max(1, visible.Height * pixels.Height / wanted.Height);

        var rgb = new byte[width * height * 3];

        for (var y = 0; y < height; y += 1)
        {
            for (var x = 0; x < width; x += 1)
            {
                var (red, green, blue) = pixels[left + x, top + y];
                var offset = ((y * width) + x) * 3;

                rgb[offset] = red;
                rgb[offset + 1] = green;
                rgb[offset + 2] = blue;
            }
        }

        return new TuiPixels(width, height, rgb);
    }
}
