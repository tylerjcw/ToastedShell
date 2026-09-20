using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// The shell measures text the way a terminal draws it — <c>TUI-0005</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="StyledText.GetVisibleLength"/> returned UTF-16 code units, which is the
/// right answer for ASCII and wrong for everything else a terminal renders. Every caller
/// that pads to a width — the table sink, the prompt, the help renderers, inline tables —
/// took that answer, so a row with CJK in it pushed the right border of its box out by
/// one column per wide character.
/// </para>
/// <para>
/// The TUI had the same bug and fixed it under <c>TUI-0012</c>; these callers kept the old
/// answer only because the measurement lived in a project above them. <c>TextMeasure</c>
/// now sits beside <see cref="StyledText"/>, so there is one measurement rather than two
/// that disagree. These tests pin the shell's entry points to it.
/// </para>
/// </remarks>
public sealed class ShellTextWidthTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("abc", 3)]
    [InlineData("日本語", 6)]              // three code units, six columns
    [InlineData("😀", 2)]                  // two code units, two columns — agrees by luck
    [InlineData("😀emoji", 7)]
    [InlineData("é", 1)]             // a combining mark adds nothing
    public void Visible_length_counts_columns_not_code_units(string text, int expected)
        => Assert.Equal(expected, StyledText.GetVisibleLength(text));

    /// <summary>
    /// Escapes still contribute nothing. This was the one case the old implementation got
    /// right, and it has to survive the measurement changing underneath it.
    /// </summary>
    [Fact]
    public void Ansi_escapes_are_not_counted()
    {
        Assert.Equal(3, StyledText.GetVisibleLength("\x1b[31mabc\x1b[0m"));
        Assert.Equal(6, StyledText.GetVisibleLength("\x1b[31m日本語\x1b[0m"));
    }

    /// <summary>The shell's measure and the one the TUI draws with are the same one.</summary>
    [Theory]
    [InlineData("abc")]
    [InlineData("日本語")]
    [InlineData("😀emoji")]
    [InlineData("ｆｕｌｌｗｉｄｔｈ")]
    public void The_shell_and_the_tui_agree(string text)
        => Assert.Equal(TextMeasure.MeasureWidth(text), StyledText.GetVisibleLength(text));

    /// <summary>
    /// The cut was `plain[..width - 1]` — a column budget used as a character index. On CJK
    /// that returned twice the columns asked for, which is what pushed a table's border out.
    /// </summary>
    [Theory]
    [InlineData("日本語日本語日本語", 10)]
    [InlineData("abcdefghijklmnop", 10)]
    [InlineData("😀😀😀😀😀😀", 7)]
    [InlineData("日本語", 2)]
    public void A_clipped_cell_never_exceeds_its_column_budget(string value, int width)
        => Assert.True(
            StyledText.GetVisibleLength(InlineTablePlan.ClipCell(value, width)) <= width,
            $"'{value}' clipped to {width} measured " +
            $"{StyledText.GetVisibleLength(InlineTablePlan.ClipCell(value, width))}");

    /// <summary>
    /// Cutting mid-surrogate produced half an emoji, which a terminal draws as a
    /// replacement box rather than as the character that was there.
    /// </summary>
    [Fact]
    public void A_clipped_cell_never_splits_a_character()
    {
        foreach (var width in Enumerable.Range(1, 12))
        {
            var clipped = InlineTablePlan.ClipCell("😀日本語😀", width);

            Assert.DoesNotContain('�', clipped);
            Assert.False(
                clipped.Length > 0 && char.IsHighSurrogate(clipped[^1]),
                $"width {width} left a dangling high surrogate");
        }
    }

    /// <summary>Text that fits is returned untouched, styling included.</summary>
    [Fact]
    public void A_cell_that_fits_keeps_its_styling()
    {
        const string styled = "\x1b[31mabc\x1b[0m";

        Assert.Equal(styled, InlineTablePlan.ClipCell(styled, 10));
    }

    /// <summary>
    /// The whole point, end to end: every line of a rendered table occupies the same number
    /// of columns.
    /// </summary>
    /// <remarks>
    /// The unit tests above pin the pieces; this pins the thing the reader sees. A long CJK
    /// value exercises the wrap, which measured its budget in characters and so handed a
    /// 36-column cell 36 CJK characters — 72 columns — and pushed the border out.
    /// </remarks>
    [Theory]
    [InlineData("日本語")]
    [InlineData("日本語日本語日本語日本語日本語日本語日本語日本語日本語日本語日本語日本語")]
    [InlineData("😀emoji")]
    [InlineData("plain ascii")]
    public void Every_line_of_a_rendered_table_is_the_same_width(string value)
    {
        var display = new DisplayEngine(
            new ObjectFormatter(DisplayProfileRegistry.CreateDefault(new DisplayPreferences())));

        var text = display.RenderMany([new WidthRow(value), new WidthRow("abc")]);

        var widths = text
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0)
            .Select(StyledText.GetVisibleLength)
            .Distinct()
            .ToList();

        Assert.True(
            widths.Count == 1,
            $"lines of differing widths ({string.Join(", ", widths)}):\n{text}");
    }

    private sealed record WidthRow(string Name);

    /// <summary>
    /// Padding and clipping have to agree, or a cell is right by one measure and wrong by
    /// the other — which is the shape of the original bug.
    /// </summary>
    [Theory]
    [InlineData("日本語", 12)]
    [InlineData("abc", 12)]
    [InlineData("😀emoji", 12)]
    [InlineData("日本語日本語日本語日本語", 12)]
    public void A_padded_cell_occupies_exactly_its_width(string value, int width)
        => Assert.Equal(
            width,
            StyledText.GetVisibleLength(InlineTablePlan.PadRight(InlineTablePlan.ClipCell(value, width), width)));
}
