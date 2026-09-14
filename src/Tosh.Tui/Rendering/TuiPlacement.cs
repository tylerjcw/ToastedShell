namespace Tosh.Tui.Rendering;

/// <summary>
/// Pixels the terminal itself is asked to draw, over a rectangle of cells.
/// </summary>
/// <remarks>
/// <para>
/// Half blocks put a picture in the cell grid, which is why they always work — a cell is
/// a cell and the diff, the clipping and the snapshots all already understand one. Real
/// pixels do not fit in a cell, so they travel beside the grid rather than in it: the
/// widget says where it wants a picture, the writer decides how to ask for it, and the
/// terminal draws it on top (<c>TUI-0025</c>).
/// </para>
/// <para>
/// <see cref="Id"/> is what makes a placement a thing rather than an event. A terminal
/// keeps the image it was given until it is told to delete it, so a picture that moved,
/// closed or was covered has to be taken back — which means the writer has to know what
/// was on screen last frame and is not now. That is a diff, and a diff needs identity.
/// </para>
/// </remarks>
/// <param name="Id">Stable for the lifetime of the widget that asked for it.</param>
/// <param name="Column">Where the picture starts, in buffer cells.</param>
/// <param name="Pixels">Already cropped to what is visible: the writer does no geometry.</param>
public sealed record TuiPlacement(
    int Id,
    int Column,
    int Row,
    int Columns,
    int Rows,
    TuiPixels Pixels)
{
    /// <summary>Whether two placements would put the same picture in the same place.</summary>
    /// <remarks>
    /// Compared rather than re-sent, because re-sending a 4K frame every time anything on
    /// the screen changes is the difference between a preview pane and a slideshow. The
    /// pixels are compared by reference: a loader that hands back the same object for the
    /// same file — which the shell's does, out of its cache — makes this free, and one
    /// that does not merely re-sends.
    /// </remarks>
    public bool SameAs(TuiPlacement? other)
        => other is not null &&
           other.Id == Id &&
           other.Column == Column &&
           other.Row == Row &&
           other.Columns == Columns &&
           other.Rows == Rows &&
           ReferenceEquals(other.Pixels, Pixels);
}
