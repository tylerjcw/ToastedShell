using System.Text;
using Tosh.Runtime;

namespace Tosh.Tui.Rendering;

/// <summary>
/// Turns a <see cref="TuiBuffer"/> into the bytes a terminal needs, writing only what
/// changed since the last frame.
/// </summary>
/// <remarks>
/// <para>
/// The runtime used to clear the screen and print the whole frame on every render. That
/// was invisible while screens only redrew on a keystroke, and became a full repaint per
/// second once screens could refresh themselves (<c>TUI-0001</c>). Comparing against the
/// previously presented buffer means a clock ticking in a corner writes the corner.
/// </para>
/// <para>
/// Two things make the output smaller than a naive per-cell write: runs of adjacent
/// changed cells share one cursor move, and styling is emitted only when it differs from
/// the cell before it.
/// </para>
/// </remarks>
public static class TuiTerminalWriter
{
    private const string Reset = "\x1b[0m";

    /// <summary>Everything needed to draw <paramref name="next"/> from scratch.</summary>
    public static string Present(TuiBuffer next) => Present(null, next);

    /// <summary>
    /// The difference between two frames, or a full paint when
    /// <paramref name="previous"/> is absent or a different size.
    /// </summary>
    public static string Present(TuiBuffer? previous, TuiBuffer next)
    {
        ArgumentNullException.ThrowIfNull(next);

        // A resized terminal shares no addresses with the frame before it.
        var reusable = previous is not null && previous.Size == next.Size;

        // Where a picture used to be and is not any more. Under Kitty this is empty, because
        // deleting a placement reveals the cells underneath; under sixels a picture is
        // painted into the screen like text, so the only thing that removes it is those
        // cells being written again — and the diff would otherwise skip them, because their
        // *content* has not changed even though what the reader sees has.
        var repaint = Vacated(previous?.Placements ?? [], next.Placements, reusable);
        var builder = new StringBuilder();

        if (!reusable)
        {
            // Reset before clearing so the terminal's state at the start of the first
            // frame is known rather than inherited from whatever ran before.
            builder.Append(Reset).Append("\x1b[2J\x1b[H");
        }

        // Every frame ends with the terminal back in its default styling — see the reset
        // at the end — so a diff frame can start from that assumption instead of
        // re-stating a style that is already in effect.
        var style = TuiStyle.Default;

        for (var row = 0; row < next.Height; row += 1)
        {
            var column = 0;

            while (column < next.Width)
            {
                if (reusable && !Differs(previous!, next, column, row) && !Covers(repaint, column, row))
                {
                    column += 1;
                    continue;
                }

                // A run of changed cells costs one cursor move between them all.
                var runStart = column;
                builder.Append($"\x1b[{row + 1};{runStart + 1}H");

                while (column < next.Width &&
                       (!reusable || Differs(previous!, next, column, row) || Covers(repaint, column, row)))
                {
                    var cell = next[column, row];

                    if (cell.Style != style)
                    {
                        // Reset only when something is actually set, so an unstyled run
                        // costs nothing; introduce only when the new style is not default.
                        if (!style.IsDefault)
                        {
                            builder.Append(Reset);
                        }

                        if (!cell.Style.IsDefault)
                        {
                            builder.Append(Introducer(cell.Style));
                        }

                        style = cell.Style;
                    }

                    // A continuation carries no text: the wide character in the cell
                    // before it already covered this column.
                    if (!cell.IsContinuation)
                    {
                        builder.Append(cell.Text);
                    }

                    column += 1;
                }
            }
        }

        if (!style.IsDefault)
        {
            builder.Append(Reset);
        }

        // Pictures after the cells, because a placement lands at the cursor and the cursor
        // is wherever the last cell written left it — and before the caret, because the
        // caret is the last thing said about a frame.
        //
        // The previous frame's pictures are passed even when its cells cannot be reused. A
        // resize repaints every cell and repaints no pixels: the terminal is still holding
        // every picture it was given, and something has to say otherwise.
        AppendPictures(builder, previous?.Placements ?? [], next.Placements, reusable);

        AppendCursor(builder, reusable ? previous!.Cursor : null, next.Cursor, reusable);

        return builder.ToString();
    }

    /// <summary>
    /// Places or hides the cursor, emitting nothing when neither changed.
    /// </summary>
    /// <remarks>
    /// The position has to be re-stated whenever anything was drawn, because drawing
    /// leaves the cursor wherever the last run ended.
    /// </remarks>
    private static void AppendCursor(
        StringBuilder builder,
        (int Column, int Row)? previous,
        (int Column, int Row)? next,
        bool reusable)
    {
        if (next is null)
        {
            if (!reusable || previous is not null)
            {
                builder.Append("\x1b[?25l");
            }

            return;
        }

        builder.Append($"\x1b[{next.Value.Row + 1};{next.Value.Column + 1}H");

        if (!reusable || previous is null)
        {
            builder.Append("\x1b[?25h");
        }
    }

    /// <summary>
    /// The rectangles a picture has left, which have to be drawn again.
    /// </summary>
    /// <remarks>
    /// Empty for a protocol that remembers its placements, because deleting one is enough.
    /// A sixel is not remembered — it was painted into the screen — so the cells it covered
    /// are stale in a way the cell diff cannot see: they hold exactly what they held last
    /// frame, and what the reader sees over them is a photograph.
    /// </remarks>
    private static IReadOnlyList<TuiRect> Vacated(
        IReadOnlyList<TuiPlacement> previous,
        IReadOnlyList<TuiPlacement> next,
        bool reusable)
    {
        if (previous.Count == 0 || TuiGraphics.Remembers(TuiGraphics.Protocol))
        {
            return [];
        }

        var gone = new List<TuiRect>();

        foreach (var was in previous)
        {
            if (!reusable || !next.Any(now => now.SameAs(was)))
            {
                gone.Add(new TuiRect(was.Column, was.Row, was.Columns, was.Rows));
            }
        }

        return gone;
    }

    private static bool Covers(IReadOnlyList<TuiRect> regions, int column, int row)
    {
        for (var index = 0; index < regions.Count; index += 1)
        {
            if (regions[index].Contains(column, row))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Sends the pictures that are new or have moved, and takes back the ones that are gone.
    /// </summary>
    /// <remarks>
    /// A terminal keeps an image until it is told to delete it, so a frame that no longer
    /// asks for one has to say so — otherwise a preview stays on screen over whatever
    /// replaced it. Re-sending an unchanged picture is the other failure: a 4K frame down
    /// the wire every time a clock ticks is a slideshow, not a preview.
    /// </remarks>
    private static void AppendPictures(
        StringBuilder builder,
        IReadOnlyList<TuiPlacement> previous,
        IReadOnlyList<TuiPlacement> next,
        bool reusable)
    {
        // Kept only if this frame asks for exactly the same picture in exactly the same
        // place. Matching on the id alone was the bug: a preview pane showing a new file
        // reuses its widget and therefore its id, so the old picture was never taken back
        // and the new one was drawn over it — leaving the taller one's edges showing above
        // and below, stacked, until something else repainted.
        foreach (var was in previous)
        {
            if (!reusable || !next.Any(now => now.SameAs(was)))
            {
                builder.Append(TuiGraphics.Delete(was.Id));
            }
        }

        foreach (var now in next)
        {
            if (!reusable || !previous.Any(was => was.SameAs(now)))
            {
                builder.Append(TuiGraphics.Transmit(now));
            }
        }
    }

    private static bool Differs(TuiBuffer previous, TuiBuffer next, int column, int row)
        => previous[column, row] != next[column, row];

    private static string Introducer(TuiStyle style)
        => style.IsDefault
            ? string.Empty
            : StyledText.BuildSgrIntroducer(
                style.Foreground,
                style.Background,
                style.Attributes.HasFlag(TuiTextAttributes.Bold),
                style.Attributes.HasFlag(TuiTextAttributes.Italic),
                style.Attributes.HasFlag(TuiTextAttributes.Underline),
                style.Attributes.HasFlag(TuiTextAttributes.Dim),
                style.Attributes.HasFlag(TuiTextAttributes.Reverse));
}
