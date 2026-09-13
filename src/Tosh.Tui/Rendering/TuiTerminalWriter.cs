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
                if (reusable && !Differs(previous!, next, column, row))
                {
                    column += 1;
                    continue;
                }

                // A run of changed cells costs one cursor move between them all.
                var runStart = column;
                builder.Append($"\x1b[{row + 1};{runStart + 1}H");

                while (column < next.Width && (!reusable || Differs(previous!, next, column, row)))
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
