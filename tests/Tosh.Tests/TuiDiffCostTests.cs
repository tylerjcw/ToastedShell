using Tosh.Tui;
using Tosh.Tui.Rendering;

namespace Tosh.Tests;

/// <summary>
/// What a redraw costs to send (<c>TUI-0012</c>).
/// </summary>
/// <remarks>
/// The budget line the diffing writer exists for: a clock ticking in a corner should write
/// the corner. A benchmark records the number; these assert the shape of it, so a change
/// that quietly went back to full repaints fails here rather than being noticed as a slow
/// terminal six months later.
/// </remarks>
public sealed class TuiDiffCostTests
{
    private static TuiBuffer Filled(int width, int height, string word = "cell")
    {
        var buffer = new TuiBuffer(new TuiSize(width, height));

        for (var row = 0; row < height; row += 1)
        {
            buffer.DrawText(0, row, string.Concat(Enumerable.Repeat(word, (width / word.Length) + 1)), default);
        }

        return buffer;
    }

    /// <summary>A frame where nothing moved writes nothing at all.</summary>
    [Fact]
    public void An_unchanged_frame_writes_nothing()
    {
        var previous = Filled(120, 40);
        var next = Filled(120, 40);

        Assert.Equal(string.Empty, TuiTerminalWriter.Present(previous, next));
    }

    /// <summary>
    /// A redraw that changes one row writes bytes proportional to that row.
    /// </summary>
    /// <remarks>
    /// Stated as a ratio against the full repaint rather than as an absolute, because the
    /// absolute depends on how the cursor moves and what styling is in play — and the claim
    /// being made is about proportionality, not about a particular number of bytes.
    /// </remarks>
    [Fact]
    public void A_redraw_that_changes_one_row_writes_that_row()
    {
        var previous = Filled(120, 40);
        var next = Filled(120, 40);

        next.DrawText(0, 17, "this row is different now", default);

        var whole = TuiTerminalWriter.Present(next).Length;
        var diff = TuiTerminalWriter.Present(previous, next).Length;

        // One row of forty, with room for the cursor move and a margin: well under a tenth
        // of the frame, where a full repaint would be all of it.
        Assert.True(
            diff * 10 < whole,
            $"a one-row change wrote {diff} bytes against {whole} for the whole frame");
    }

    /// <summary>
    /// Changing one cell writes about one cell.
    /// </summary>
    /// <remarks>
    /// The strongest version of the same property. A writer that emitted whole rows would
    /// pass the test above and fail this one.
    /// </remarks>
    [Fact]
    public void A_redraw_that_changes_one_cell_writes_about_one_cell()
    {
        var previous = Filled(120, 40);
        var next = Filled(120, 40);

        next.DrawText(63, 17, "X", default);

        // A cursor move and a character. Generous, because the exact escape depends on the
        // coordinates — but nowhere near a row, which would be 120.
        Assert.InRange(TuiTerminalWriter.Present(previous, next).Length, 1, 32);
    }

    /// <summary>
    /// A resized terminal is repainted whole, because nothing lines up any more.
    /// </summary>
    /// <remarks>
    /// The exception that makes the rule safe: the previous buffer's addresses mean
    /// something else at the new size, so diffing against it would leave the screen a
    /// mixture of two layouts.
    /// </remarks>
    [Fact]
    public void A_resize_is_not_diffed()
    {
        var previous = Filled(80, 24);
        var next = Filled(120, 40);

        var resized = TuiTerminalWriter.Present(previous, next);

        Assert.Contains("[2J", resized, StringComparison.Ordinal);
    }

    /// <summary>
    /// Styling is emitted when it changes, not per cell.
    /// </summary>
    /// <remarks>
    /// A writer that introduced the style before every character would be correct and would
    /// multiply the size of every frame by the length of an SGR sequence.
    /// </remarks>
    [Fact]
    public void A_run_of_one_style_is_introduced_once()
    {
        var buffer = new TuiBuffer(new TuiSize(40, 1));

        buffer.DrawText(0, 0, new string('x', 40), new TuiStyle(Foreground: "cyan"));

        var written = TuiTerminalWriter.Present(buffer);

        Assert.Equal(1, Occurrences(written, "[36m"));

        static int Occurrences(string text, string needle)
        {
            var count = 0;

            for (var at = text.IndexOf(needle, StringComparison.Ordinal);
                 at >= 0;
                 at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
            {
                count += 1;
            }

            return count;
        }
    }
}
