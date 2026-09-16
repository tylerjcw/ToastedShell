namespace Tosh.Tui.Rendering;

/// <summary>
/// Draws the thumb that says where you are in something taller than the screen.
/// </summary>
/// <remarks>
/// <para>
/// One implementation, in the rendering layer, because a scrollbar is a function of three
/// numbers — where you are, how much there is, how much shows — and every widget that
/// scrolls has all three. Nine widgets each keeping a <c>TuiScrollState</c> and drawing
/// their own is what <c>TUI-0007</c> is about.
/// </para>
/// <para>
/// Off by default on every widget that offers it. A bar drawn down the edge of a pane the
/// reader can already see the whole of is noise.
/// </para>
/// </remarks>
public static class TuiScrollbar
{
    /// <summary>The thumb and the track, as they appear on a terminal with no colour.</summary>
    private const char Thumb = '█';
    private const char Track = '│';

    /// <summary>
    /// Draws the bar if one is wanted and needed, and answers with the room left for the
    /// content.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reserving the column and drawing the bar are one decision, so they are one call.
    /// Written out at each widget they were two — <c>Scrollbar &amp;&amp; count &gt;
    /// height</c> to reserve, and <c>Scrollbar</c> to draw — which agreed only because
    /// <see cref="DrawVertical"/> draws nothing when everything fits. Two conditions that
    /// have to agree, four times over, is a bug waiting for someone to change one of them.
    /// </para>
    /// <para>
    /// The bar goes down first because the content never reaches the column it takes, so
    /// the order costs nothing and the caller is left with one line instead of five.
    /// </para>
    /// </remarks>
    /// <param name="wanted">Whether this widget draws a bar at all.</param>
    /// <param name="firstRow">
    /// The first row the bar covers. A table's runs beside its rows and not beside its
    /// header, which is the one thing about this that differs between widgets.
    /// </param>
    /// <param name="visibleRows">
    /// How many rows of content are on screen, when that is not the surface's height —
    /// again a table, whose header takes one of them.
    /// </param>
    /// <returns>The surface the content should draw into.</returns>
    public static TuiSurface Fit(
        TuiSurface surface,
        bool wanted,
        int offset,
        int contentLength,
        TuiStyle style = default,
        int firstRow = 0,
        int? visibleRows = null)
    {
        var shows = visibleRows ?? Math.Max(0, surface.Height - firstRow);

        if (!wanted || contentLength <= shows || surface.Width <= 0 || surface.Height <= firstRow)
        {
            return surface;
        }

        DrawVertical(
            surface.Clip(new TuiRect(0, firstRow, surface.Width, surface.Height - firstRow)),
            offset,
            contentLength,
            style,
            style);

        return surface.Clip(new TuiRect(0, 0, surface.Width - 1, surface.Height));
    }

    /// <summary>
    /// Draws a vertical bar down the last column of <paramref name="surface"/>.
    /// </summary>
    /// <remarks>
    /// Nothing is drawn when everything fits, which is the common case and the one where a
    /// bar would be a lie.
    /// </remarks>
    public static void DrawVertical(
        TuiSurface surface,
        int offset,
        int contentLength,
        TuiStyle thumbStyle = default,
        TuiStyle trackStyle = default)
    {
        var height = surface.Height;

        if (height <= 0 || surface.Width <= 0 || contentLength <= height)
        {
            return;
        }

        // At least one row, so the thumb is visible in a very long document, and never so
        // long that it fills a track it could still move within.
        var thumbHeight = Math.Clamp(height * height / contentLength, 1, height - 1);

        var travel = height - thumbHeight;
        var maxOffset = Math.Max(1, contentLength - height);
        var thumbTop = (int)Math.Round((double)offset / maxOffset * travel, MidpointRounding.AwayFromZero);

        thumbTop = Math.Clamp(thumbTop, 0, travel);

        var column = surface.Width - 1;

        for (var row = 0; row < height; row += 1)
        {
            var inThumb = row >= thumbTop && row < thumbTop + thumbHeight;

            surface.Set(column, row, new TuiCell(
                (inThumb ? Thumb : Track).ToString(),
                inThumb ? thumbStyle : trackStyle));
        }
    }
}
