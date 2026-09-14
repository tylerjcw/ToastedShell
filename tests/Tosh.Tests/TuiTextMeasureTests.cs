using Tosh.Tui.Rendering;

namespace Tosh.Tests;

/// <summary>
/// Width measurement, against the cases that made counting code units wrong.
/// </summary>
public sealed class TuiTextMeasureTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("hello", 5)]
    [InlineData("│─╮", 3)]            // box drawing is narrow
    public void Ascii_and_box_drawing_measure_one_column_each(string text, int expected)
    {
        Assert.Equal(expected, TuiTextMeasure.MeasureWidth(text));
    }

    [Fact]
    public void Cjk_occupies_two_columns_per_character()
    {
        // Three characters, six columns — but only three UTF-16 code units, which is
        // what the old measurement returned.
        Assert.Equal(6, TuiTextMeasure.MeasureWidth("日本語"));
        Assert.Equal(3, "日本語".Length);
    }

    [Fact]
    public void Emoji_occupies_two_columns_however_many_code_units_it_takes()
    {
        Assert.Equal(2, TuiTextMeasure.MeasureWidth("😀"));

        // A family emoji: several emoji joined by zero-width joiners. One thing on
        // screen, five code units.
        var family = "\U0001F469‍\U0001F4BB";
        Assert.Equal(2, TuiTextMeasure.MeasureWidth(family));
        Assert.True(family.Length > 2);
    }

    [Fact]
    public void A_combining_mark_adds_no_width()
    {
        var composed = "é";           // é as one code point
        var decomposed = "é";        // e followed by a combining acute

        Assert.Equal(1, TuiTextMeasure.MeasureWidth(composed));
        Assert.Equal(1, TuiTextMeasure.MeasureWidth(decomposed));
    }

    [Fact]
    public void An_emoji_presentation_selector_widens_its_character()
    {
        Assert.Equal(1, TuiTextMeasure.MeasureWidth("❤"));          // ❤ as a dingbat
        Assert.Equal(2, TuiTextMeasure.MeasureWidth("❤️"));    // ❤️ as an emoji
    }

    [Fact]
    public void Clusters_are_whole_characters_not_code_units()
    {
        Assert.Equal(["a", "😀", "é"], TuiTextMeasure.EnumerateClusters("a😀é"));
    }

    [Fact]
    public void Truncating_never_splits_a_cluster()
    {
        // Each is two columns, so four fit in five and the fifth column goes unused
        // rather than being filled with half a character.
        Assert.Equal("日本", TuiTextMeasure.Truncate("日本語", 5));
        Assert.Equal("日本語", TuiTextMeasure.Truncate("日本語", 6));
    }

    [Fact]
    public void Truncating_keeps_a_combining_mark_with_its_base()
    {
        Assert.Equal("é", TuiTextMeasure.Truncate("éx", 1));
    }

    [Fact]
    public void Truncating_to_nothing_returns_nothing()
    {
        Assert.Equal(string.Empty, TuiTextMeasure.Truncate("anything", 0));
        Assert.Equal(string.Empty, TuiTextMeasure.Truncate("anything", -3));
    }

    [Theory]
    [InlineData("hello", 10, "hello")]
    [InlineData("hello", 5, "hello")]
    [InlineData("hello", 4, "hel\u2026")]
    [InlineData("hello", 1, "\u2026")]
    [InlineData("hello", 0, "")]
    [InlineData("", 4, "")]
    public void Text_that_does_not_fit_is_cut_and_says_so(string text, int columns, string expected)
        => Assert.Equal(expected, TuiTextMeasure.Elide(text, columns));

    [Fact]
    public void A_cut_counts_columns_rather_than_characters()
    {
        // Two columns for the emoji, one for the mark: three columns of "ab" is
        // exactly what a cell grid can show, and the mark has to be paid for out of them.
        var elided = TuiTextMeasure.Elide("\U0001F680ab", 3);

        Assert.Equal(3, TuiTextMeasure.MeasureWidth(elided));
        Assert.EndsWith("\u2026", elided, StringComparison.Ordinal);
    }
}
