using Tosh.Runtime;
using Tosh.Tui;
using Tosh.Tui.Rendering;

namespace Tosh.Tests;

/// <summary>
/// What a terminal gets when its encoding cannot carry a screen (<c>TUI-0009</c>).
/// </summary>
/// <remarks>
/// Every border, guide, meter and spinner is drawn out of characters above U+2000, so on a
/// terminal that cannot show them the whole screen is replacement boxes — not a degraded
/// screen but an unreadable one.
/// </remarks>
public sealed class TuiGlyphsTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] entries)
        => name => entries.FirstOrDefault(entry => entry.Name == name).Value;

    /// <summary>
    /// Unicode is assumed unless something says otherwise.
    /// </summary>
    /// <remarks>
    /// The polarity matters more than the detection. A great many correct setups leave
    /// <c>LANG</c> unset, and defaulting those to ASCII would make a working screen worse
    /// in order to protect one that was not broken.
    /// </remarks>
    [Theory]
    [InlineData("en_GB.UTF-8", true)]
    [InlineData("en_US.utf8", true)]
    [InlineData("C", false)]
    [InlineData("POSIX", false)]
    [InlineData("en_US.ISO-8859-1", false)]
    [InlineData("", true)]
    public void The_locale_says_whether_the_characters_will_arrive(string lang, bool expected)
        => Assert.Equal(expected, TuiGlyphs.Supported(Env(("LANG", lang))));

    [Fact]
    public void LC_ALL_outranks_LANG_the_way_the_locale_rules_say()
        => Assert.False(TuiGlyphs.Supported(Env(("LC_ALL", "C"), ("LANG", "en_GB.UTF-8"))));

    [Fact]
    public void LC_CTYPE_sits_between_them()
        => Assert.True(TuiGlyphs.Supported(Env(("LC_CTYPE", "en_GB.UTF-8"), ("LANG", "C"))));

    [Fact]
    public void A_dumb_terminal_gets_ascii()
        => Assert.False(TuiGlyphs.Supported(Env(("TERM", "dumb"), ("LANG", "en_GB.UTF-8"))));

    [Theory]
    [InlineData("1", false)]
    [InlineData("yes", false)]
    [InlineData("0", true)]
    public void The_reader_has_the_last_word(string value, bool expected)
        => Assert.Equal(
            expected,
            TuiGlyphs.Supported(Env(("TOSH_TUI_ASCII", value), ("LANG", "en_GB.UTF-8"))));

    [Theory]
    [InlineData("─", "-")]
    [InlineData("━", "-")]
    [InlineData("║", "|")]
    [InlineData("╭", "+")]
    [InlineData("┼", "+")]
    [InlineData("█", "#")]
    [InlineData("▄", "#")]
    [InlineData("▒", ":")]
    [InlineData("▸", ">")]
    [InlineData("▾", "v")]
    [InlineData("·", ".")]
    [InlineData("…", ".")]
    [InlineData("⠋", "*")]
    public void A_drawn_character_folds_to_something_a_terminal_has(string glyph, string expected)
        => Assert.Equal(expected, TuiGlyphs.Fold(glyph));

    /// <summary>
    /// Only what the framework draws. A reader's own text is left alone.
    /// </summary>
    /// <remarks>
    /// Mangling somebody's filename to protect a border is the wrong way round, and a
    /// terminal that cannot show it will say so itself.
    /// </remarks>
    [Theory]
    [InlineData("a")]
    [InlineData("日")]
    [InlineData("é")]
    [InlineData("😀")]
    public void Text_that_the_framework_did_not_draw_is_not_folded(string text)
        => Assert.Equal(text, TuiGlyphs.Fold(text));

    /// <summary>
    /// Every replacement is one column, because the layout already reserved the columns.
    /// </summary>
    /// <remarks>
    /// This is the invariant the whole thing rests on. A fold that changed a width would
    /// leave the row short and every border after it out of line, which is the fault this
    /// exists to prevent arriving by another door — and <c>…</c> to <c>...</c> is exactly
    /// the tempting, wrong version.
    /// </remarks>
    [Fact]
    public void Folding_never_changes_how_many_columns_a_character_takes()
    {
        foreach (var (glyph, ascii) in TuiGlyphs.Table)
        {
            Assert.Equal(TextMeasure.MeasureWidth(glyph), TextMeasure.MeasureWidth(ascii));
            Assert.All(ascii, character => Assert.InRange(character, ' ', '~'));
        }
    }

    /// <summary>A wide character is never folded, whatever it is.</summary>
    [Fact]
    public void A_two_column_character_is_left_alone()
    {
        // Folding one of these to a single ASCII character would shorten the row by a
        // column, and the continuation cell beside it has already claimed the other.
        Assert.Equal("❤️", TuiGlyphs.Fold("❤️"));
        Assert.Equal("日", TuiGlyphs.Fold("日"));
    }

    /// <summary>The same frame, drawn both ways.</summary>
    [Fact]
    public void A_frame_is_written_in_ascii_when_the_terminal_cannot_do_better()
    {
        var buffer = new TuiBuffer(new TuiSize(5, 1));

        buffer.DrawText(0, 0, "╭───╮", default);

        Assert.Contains("╭───╮", TuiTerminalWriter.Present(buffer, TuiColorDepth.TrueColor), StringComparison.Ordinal);

        var ascii = TuiTerminalWriter.Present(buffer, TuiColorDepth.None, unicode: false);

        Assert.Contains("+---+", ascii, StringComparison.Ordinal);
        Assert.DoesNotContain("╭", ascii, StringComparison.Ordinal);
    }
}
