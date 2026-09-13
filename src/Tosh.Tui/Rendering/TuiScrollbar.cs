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
