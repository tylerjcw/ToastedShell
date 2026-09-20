using Tosh.Runtime;
using Tosh.Tui.Rendering;

namespace Tosh.Tests;

/// <summary>
/// Width measurement, against the cases that made counting code units wrong.
/// </summary>
public sealed class TextMeasureTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("hello", 5)]
    [InlineData("│─╮", 3)]            // box drawing is narrow
    public void Ascii_and_box_drawing_measure_one_column_each(string text, int expected)
    {
        Assert.Equal(expected, TextMeasure.MeasureWidth(text));
    }

    [Fact]
    public void Cjk_occupies_two_columns_per_character()
    {
        // Three characters, six columns — but only three UTF-16 code units, which is
        // what the old measurement returned.
        Assert.Equal(6, TextMeasure.MeasureWidth("日本語"));
        Assert.Equal(3, "日本語".Length);
    }

    [Fact]
    public void Emoji_occupies_two_columns_however_many_code_units_it_takes()
    {
        Assert.Equal(2, TextMeasure.MeasureWidth("😀"));

        // A family emoji: several emoji joined by zero-width joiners. One thing on
        // screen, five code units.
        var family = "\U0001F469‍\U0001F4BB";
        Assert.Equal(2, TextMeasure.MeasureWidth(family));
        Assert.True(family.Length > 2);
    }

    [Fact]
    public void A_combining_mark_adds_no_width()
    {
        var composed = "é";           // é as one code point
        var decomposed = "é";        // e followed by a combining acute

        Assert.Equal(1, TextMeasure.MeasureWidth(composed));
        Assert.Equal(1, TextMeasure.MeasureWidth(decomposed));
    }

    [Fact]
    public void An_emoji_presentation_selector_widens_its_character()
    {
        Assert.Equal(1, TextMeasure.MeasureWidth("❤"));          // ❤ as a dingbat
        Assert.Equal(2, TextMeasure.MeasureWidth("❤️"));    // ❤️ as an emoji
    }

    [Fact]
    public void Clusters_are_whole_characters_not_code_units()
    {
        Assert.Equal(["a", "😀", "é"], TextMeasure.EnumerateClusters("a😀é"));
    }

    [Fact]
    public void Truncating_never_splits_a_cluster()
    {
        // Each is two columns, so four fit in five and the fifth column goes unused
        // rather than being filled with half a character.
        Assert.Equal("日本", TextMeasure.Truncate("日本語", 5));
        Assert.Equal("日本語", TextMeasure.Truncate("日本語", 6));
    }

    [Fact]
    public void Truncating_keeps_a_combining_mark_with_its_base()
    {
        Assert.Equal("é", TextMeasure.Truncate("éx", 1));
    }

    [Fact]
    public void Truncating_to_nothing_returns_nothing()
    {
        Assert.Equal(string.Empty, TextMeasure.Truncate("anything", 0));
        Assert.Equal(string.Empty, TextMeasure.Truncate("anything", -3));
    }

    [Theory]
    [InlineData("hello", 10, "hello")]
    [InlineData("hello", 5, "hello")]
    [InlineData("hello", 4, "hel\u2026")]
    [InlineData("hello", 1, "\u2026")]
    [InlineData("hello", 0, "")]
    [InlineData("", 4, "")]
    public void Text_that_does_not_fit_is_cut_and_says_so(string text, int columns, string expected)
        => Assert.Equal(expected, TextMeasure.Elide(text, columns));

    [Fact]
    public void A_cut_counts_columns_rather_than_characters()
    {
        // Two columns for the emoji, one for the mark: three columns of "ab" is
        // exactly what a cell grid can show, and the mark has to be paid for out of them.
        var elided = TextMeasure.Elide("\U0001F680ab", 3);

        Assert.Equal(3, TextMeasure.MeasureWidth(elided));
        Assert.EndsWith("\u2026", elided, StringComparison.Ordinal);
    }

    // ── What makes it cheap enough to run per cell (TUI-0012) ────────

    /// <summary>
    /// Printable ASCII takes the fast path; anything that is not, does not.
    /// </summary>
    /// <remarks>
    /// The fast path answers "one column each" without walking grapheme clusters, so it has
    /// to be exactly wrong-free: a control character is zero-width and a combining mark
    /// belongs to its neighbour, and counting either as a column would report the wrong
    /// width for the line it is in.
    /// </remarks>
    [Theory]
    [InlineData("hello world", true)]
    [InlineData("~!@#$%^&*()", true)]
    [InlineData("", true)]
    [InlineData("tab\there", false)]
    [InlineData("esc\u001b[0m", false)]
    [InlineData("caf\u00e9", false)]
    [InlineData("\u65e5", false)]
    [InlineData("\U0001F680", false)]
    public void The_fast_path_is_taken_only_where_it_is_right(string text, bool expected)
        => Assert.Equal(expected, TextMeasure.IsPrintableAscii(text));

    /// <summary>
    /// The fast path and the slow path agree, which is the only thing that matters.
    /// </summary>
    /// <remarks>
    /// Asserted by measuring text that qualifies and text that does not against the same
    /// expectation, rather than by reaching past the branch: a fast path nobody can tell
    /// apart from the real answer is the whole point.
    /// </remarks>
    [Theory]
    [InlineData("hello", 5)]
    [InlineData("a b c", 5)]
    [InlineData("caf\u00e9", 4)]
    [InlineData("tab\there", 7)]
    public void Both_paths_report_the_same_width(string text, int expected)
        => Assert.Equal(expected, TextMeasure.MeasureWidth(text));

    /// <summary>
    /// A printable ASCII character is always the same string instance.
    /// </summary>
    /// <remarks>
    /// This is the mechanism, not a detail: a cell holds a string, and painting one 80x24
    /// frame allocated one per cell before there was a table to take them from. That was
    /// most of the three megabytes a frame cost.
    /// </remarks>
    [Fact]
    public void An_ascii_cell_string_comes_from_a_table_rather_than_the_heap()
    {
        Assert.Same(TextMeasure.Text("x"), TextMeasure.Text("x"));
        Assert.Same(TextMeasure.Text(" "), TextMeasure.Text(" "));

        // And the answer is still right for what the table cannot hold.
        Assert.Equal("\u65e5", TextMeasure.Text("\u65e5"));
    }

    /// <summary>The span walk sees the same clusters the string one does.</summary>
    [Theory]
    [InlineData("hello")]
    [InlineData("caf\u00e9 \U0001F680 \u65e5\u672c")]
    [InlineData("e\u0301tait")]
    public void The_allocation_free_walk_agrees_with_the_one_that_allocates(string text)
    {
        var walked = new List<string>();

        foreach (var cluster in TextMeasure.Clusters(text))
        {
            walked.Add(new string(cluster));
        }

        Assert.Equal(TextMeasure.EnumerateClusters(text), walked);
    }
}
