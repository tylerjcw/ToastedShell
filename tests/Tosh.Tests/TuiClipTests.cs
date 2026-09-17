using Tosh.Tui;

namespace Tosh.Tests;

/// <summary>
/// Clipping a line to a pane width (<c>TUI-0012</c>, <c>TUI-0005</c>).
/// </summary>
/// <remarks>
/// Called once per line per frame, which is what made its cost worth caring about: the
/// original appended one character at a time and recomputed the visible length of the whole
/// accumulated string on each pass, so it was quadratic in the width it clipped to. It also
/// counted UTF-16 code units, which is the wrong number for anything but ASCII.
/// </remarks>
public sealed class TuiClipTests
{
    [Theory]
    [InlineData("hello", 10, "hello")]
    [InlineData("hello", 5, "hello")]
    [InlineData("hello there", 8, "hello t…")]
    [InlineData("hello", 1, "…")]
    [InlineData("hello", 0, "")]
    [InlineData("", 5, "")]
    public void A_line_is_cut_to_the_width_and_says_where(string text, int width, string expected)
        => Assert.Equal(expected, TuiRenderHelpers.ClipPlain(text, width));

    /// <summary>
    /// A cut counts columns, not characters.
    /// </summary>
    /// <remarks>
    /// Each of these is two columns wide and one or two UTF-16 code units, so counting
    /// either of the wrong things gives a line that overruns its pane and pushes the border
    /// off the end of the row.
    /// </remarks>
    [Fact]
    public void Columns_are_counted_rather_than_code_units()
    {
        // Six columns of text, clipped to five: two characters and the mark.
        Assert.Equal("日本…", TuiRenderHelpers.ClipPlain("日本語", 5));

        // And it fits when it fits.
        Assert.Equal("日本語", TuiRenderHelpers.ClipPlain("日本語", 6));
    }

    /// <summary>A cut never lands inside a character.</summary>
    /// <remarks>
    /// Half a surrogate pair is not a character at all, and a terminal shows it as a
    /// replacement box — so a clipped filename ends in a question mark rather than an
    /// ellipsis.
    /// </remarks>
    [Fact]
    public void A_cut_never_splits_a_character()
    {
        var clipped = TuiRenderHelpers.ClipPlain("ab😀cd", 4);

        Assert.DoesNotContain('\ud83d', clipped);
        Assert.EndsWith("…", clipped, StringComparison.Ordinal);
    }

    /// <summary>
    /// Styling costs no columns and survives the cut.
    /// </summary>
    /// <remarks>
    /// The config browser clips highlighted source in its preview pane, so this path is
    /// real: counting the escape bytes as width would clip a coloured line to a fraction of
    /// its pane, and dropping them would clip it to the right width in the wrong colour.
    /// </remarks>
    [Fact]
    public void Escape_sequences_are_kept_and_cost_nothing()
    {
        var styled = "[31mred[0m and more";

        var clipped = TuiRenderHelpers.ClipPlain(styled, 8);

        Assert.Contains("[31m", clipped, StringComparison.Ordinal);
        Assert.Contains("[0m", clipped, StringComparison.Ordinal);
        Assert.Equal("red and…", Strip(clipped));
    }

    [Fact]
    public void A_styled_line_that_fits_is_left_exactly_as_it_was()
    {
        var styled = "[31mred[0m";

        Assert.Same(styled, TuiRenderHelpers.ClipPlain(styled, 10));
    }

    /// <summary>
    /// Padding fills to the width the caller asked for, in columns.
    /// </summary>
    /// <remarks>
    /// The pad is what keeps a box's right-hand border in the same column on every row, so
    /// it has to agree with the clip about what a column is.
    /// </remarks>
    [Theory]
    [InlineData("hi", 5, "hi   ")]
    [InlineData("日本", 5, "日本 ")]
    [InlineData("hello there", 6, "hello…")]
    public void Padding_and_clipping_agree_about_what_a_column_is(string text, int width, string expected)
        => Assert.Equal(expected, TuiRenderHelpers.TrimOrPadPlain(text, width));

    private static string Strip(string text)
        => Tosh.Runtime.StyledText.StripAnsi(text);
}
