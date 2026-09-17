using Tosh.Tui;
using Tosh.Tui.Rendering;

namespace Tosh.Tests;

/// <summary>
/// What the TUI sends a terminal that cannot show everything (<c>TUI-0009</c>).
/// </summary>
/// <remarks>
/// The failure this prevents is not a screen in the wrong colours. A terminal that does not
/// understand <c>38;2;r;g;b</c> prints part of it, so what a reader on a 16-colour terminal
/// saw was a screen with numbers and semicolons scattered through the text.
/// </remarks>
public sealed class TuiColorsTests
{
    /// <summary>An environment made of exactly what is passed, and nothing ambient.</summary>
    /// <remarks>
    /// Injected rather than set on the process, because the process environment is shared
    /// by every test in the run: a suite that sets <c>NO_COLOR</c> to check one thing turns
    /// the colour off underneath whatever is running beside it.
    /// </remarks>
    private static Func<string, string?> Env(params (string Name, string Value)[] entries)
        => name => entries.FirstOrDefault(entry => entry.Name == name).Value;

    [Theory]
    // What a modern terminal says about itself.
    [InlineData("truecolor", "xterm-256color", TuiColorDepth.TrueColor)]
    [InlineData("24bit", "xterm-256color", TuiColorDepth.TrueColor)]
    // TERM is nearly always xterm-256color, so it decides only when COLORTERM is silent.
    [InlineData("", "xterm-256color", TuiColorDepth.Ansi256)]
    [InlineData("", "screen-256color", TuiColorDepth.Ansi256)]
    [InlineData("", "xterm", TuiColorDepth.Ansi16)]
    [InlineData("", "vt100", TuiColorDepth.Ansi16)]
    // A terminal that says it is dumb is telling the truth about itself.
    [InlineData("", "dumb", TuiColorDepth.None)]
    [InlineData("", "", TuiColorDepth.None)]
    public void The_terminal_is_taken_at_its_word(string colorTerm, string term, TuiColorDepth expected)
        => Assert.Equal(expected, TuiColors.Detect(Env(("COLORTERM", colorTerm), ("TERM", term))));

    /// <summary>
    /// <c>NO_COLOR</c> means any non-empty value, which is the whole convention.
    /// </summary>
    /// <remarks>
    /// Checking for <c>"1"</c> is the mistake worth naming: a reader who exports
    /// <c>NO_COLOR=yes</c>, or <c>NO_COLOR=true</c>, or just <c>NO_COLOR=</c> with a space
    /// in it, has said what they meant and a program that demands a particular spelling has
    /// not listened. no-color.org is one paragraph long and this is what it says.
    /// </remarks>
    [Theory]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("yes")]
    [InlineData("anything at all")]
    public void NO_COLOR_is_honoured_whatever_it_is_set_to(string value)
        => Assert.Equal(
            TuiColorDepth.None,
            TuiColors.Detect(Env(("NO_COLOR", value), ("COLORTERM", "truecolor"), ("TERM", "xterm-256color"))));

    [Fact]
    public void An_empty_NO_COLOR_is_not_set_at_all()
        => Assert.Equal(
            TuiColorDepth.TrueColor,
            TuiColors.Detect(Env(("NO_COLOR", ""), ("COLORTERM", "truecolor"), ("TERM", "xterm-256color"))));

    [Theory]
    [InlineData("0", TuiColorDepth.None)]
    [InlineData("1", TuiColorDepth.Ansi16)]
    [InlineData("2", TuiColorDepth.Ansi256)]
    [InlineData("3", TuiColorDepth.TrueColor)]
    public void FORCE_COLOR_colours_a_destination_that_looks_like_it_cannot(
        string value,
        TuiColorDepth expected)
        => Assert.Equal(expected, TuiColors.Detect(Env(("FORCE_COLOR", value), ("TERM", "dumb"))));

    /// <summary>A reader whose terminal lies about itself can say so.</summary>
    [Theory]
    [InlineData("none", TuiColorDepth.None)]
    [InlineData("16", TuiColorDepth.Ansi16)]
    [InlineData("256", TuiColorDepth.Ansi256)]
    [InlineData("truecolor", TuiColorDepth.TrueColor)]
    public void The_reader_has_the_last_word(string value, TuiColorDepth expected)
        => Assert.Equal(
            expected,
            TuiColors.Detect(Env(("TOSH_TUI_COLOR", value), ("NO_COLOR", "1"), ("TERM", "dumb"))));

    [Fact]
    public void A_hex_colour_is_sent_as_itself_where_there_is_truecolor()
        => Assert.Equal(
            "\x1b[38;2;255;128;0m",
            TuiColors.Introducer(new TuiStyle(Foreground: "#ff8000"), TuiColorDepth.TrueColor));

    [Fact]
    public void A_hex_colour_becomes_a_palette_index_at_256()
    {
        // 255,128,0 is not in the cube: the nearest levels are 255, 135 and 0.
        var expected = 16 + (5 * 36) + (2 * 6) + 0;

        Assert.Equal(
            $"\x1b[38;5;{expected}m",
            TuiColors.Introducer(new TuiStyle(Foreground: "#ff8000"), TuiColorDepth.Ansi256));
    }

    /// <summary>
    /// A near-grey takes the ramp rather than the cube, which is what makes a dim theme
    /// readable at 256 colours.
    /// </summary>
    /// <remarks>
    /// The cube's greys are six steps apart. Rounding <c>#3a3a3a</c> into it lands on
    /// <c>#2f2f2f</c> or <c>#5f5f5f</c>, and a theme made mostly of near-greys turns into
    /// two of them; the twenty-four step ramp keeps them apart.
    /// </remarks>
    [Fact]
    public void A_near_grey_takes_the_ramp_rather_than_the_cube()
    {
        var code = TuiColors.Nearest256(0x3a, 0x3a, 0x3a);

        Assert.InRange(code, 232, 255);
    }

    [Fact]
    public void A_hex_colour_becomes_the_nearest_of_sixteen()
        => Assert.Equal(
            // 255,0,0 is bright red, not red: xterm's red is 205,0,0.
            "\x1b[91m",
            TuiColors.Introducer(new TuiStyle(Foreground: "#ff0000"), TuiColorDepth.Ansi16));

    [Fact]
    public void A_named_colour_needs_no_reducing_because_it_is_already_one_of_sixteen()
    {
        foreach (var depth in new[] { TuiColorDepth.Ansi16, TuiColorDepth.Ansi256, TuiColorDepth.TrueColor })
        {
            Assert.Equal("\x1b[36m", TuiColors.Introducer(new TuiStyle(Foreground: "cyan"), depth));
        }
    }

    [Fact]
    public void A_background_becomes_a_background_code()
        => Assert.Equal(
            "\x1b[46m",
            TuiColors.Introducer(new TuiStyle(Background: "cyan"), TuiColorDepth.Ansi256));

    /// <summary>
    /// With no colour at all, a background becomes reverse video rather than nothing.
    /// </summary>
    /// <remarks>
    /// This is the difference between degrading and dropping. A selected row is drawn with
    /// a background; drop it and the reader cannot see what is selected, which is not a
    /// cosmetic loss.
    /// </remarks>
    [Fact]
    public void A_background_that_cannot_be_shown_becomes_reverse_video()
        => Assert.Equal(
            "\x1b[7m",
            TuiColors.Introducer(new TuiStyle(Foreground: "white", Background: "blue"), TuiColorDepth.None));

    [Fact]
    public void A_style_that_is_already_reversed_is_not_reversed_back()
        => Assert.Equal(
            "\x1b[7m",
            TuiColors.Introducer(
                new TuiStyle(Background: "blue", Attributes: TuiTextAttributes.Reverse),
                TuiColorDepth.None));

    /// <summary>Attributes survive where colour does not.</summary>
    [Fact]
    public void Bold_and_underline_are_sent_at_every_depth()
    {
        var style = new TuiStyle(
            Foreground: "#ff8000",
            Attributes: TuiTextAttributes.Bold | TuiTextAttributes.Underline);

        Assert.Equal("\x1b[1;4m", TuiColors.Introducer(style, TuiColorDepth.None));
        Assert.StartsWith("\x1b[1;4;", TuiColors.Introducer(style, TuiColorDepth.Ansi16), StringComparison.Ordinal);
    }

    [Fact]
    public void A_default_style_says_nothing()
        => Assert.Equal(string.Empty, TuiColors.Introducer(TuiStyle.Default, TuiColorDepth.TrueColor));

    /// <summary>
    /// The same frame at each depth, which is the acceptance box in one assertion.
    /// </summary>
    /// <remarks>
    /// Asserted on what reaches the terminal rather than on the buffer, because the whole
    /// question is what the bytes look like.
    /// </remarks>
    [Fact]
    public void The_same_frame_is_written_differently_at_each_depth()
    {
        var buffer = new TuiBuffer(new TuiSize(5, 1));

        buffer.DrawText(0, 0, "hello", new TuiStyle(Foreground: "#ff8000"));

        Assert.Contains("38;2;255;128;0", TuiTerminalWriter.Present(buffer, TuiColorDepth.TrueColor), StringComparison.Ordinal);
        Assert.Contains("38;5;", TuiTerminalWriter.Present(buffer, TuiColorDepth.Ansi256), StringComparison.Ordinal);
        // Yellow rather than bright yellow: orange is 50 and 77 away from xterm's
        // 205,205,0 and 127 away from 255,255,0. Guessing the bright one is the mistake
        // the xterm values in the table exist to prevent, and this assertion was written
        // with that guess in it first.
        Assert.Contains("\x1b[33m", TuiTerminalWriter.Present(buffer, TuiColorDepth.Ansi16), StringComparison.Ordinal);

        var mono = TuiTerminalWriter.Present(buffer, TuiColorDepth.None);

        Assert.DoesNotContain("38;", mono, StringComparison.Ordinal);
        Assert.Contains("hello", mono, StringComparison.Ordinal);
    }
}
