using Tosh.Tui;

namespace Tosh.Tests;

public sealed class TextDocumentFormatterTests
{
    [Fact]
    public void Wrapping_a_paragraph_collapses_the_newlines_inside_it()
    {
        // This is the existing behaviour and it is correct for prose: a paragraph's line
        // breaks are an artefact of how it was typed, not part of the content.
        var lines = TextDocumentFormatter.WrapParagraph("one two\nthree four", width: 40);

        Assert.Equal(["one two three four"], lines);
    }

    [Fact]
    public void Wrapping_a_document_keeps_the_line_breaks_the_author_wrote()
    {
        var lines = TextDocumentFormatter.WrapDocument("CPU 40%\nMemory 20%", width: 40);

        Assert.Equal(["CPU 40%", "Memory 20%"], lines);
    }

    [Fact]
    public void Wrapping_a_document_keeps_blank_lines_as_gaps()
    {
        var lines = TextDocumentFormatter.WrapDocument("heading\n\nbody", width: 40);

        Assert.Equal(["heading", string.Empty, "body"], lines);
    }

    [Fact]
    public void Wrapping_a_document_still_wraps_a_line_that_is_too_long()
    {
        var lines = TextDocumentFormatter.WrapDocument("aaa bbb ccc ddd\nshort", width: 7);

        Assert.Equal(["aaa bbb", "ccc ddd", "short"], lines);
    }

    [Fact]
    public void Wrapping_a_document_normalises_carriage_returns()
    {
        var lines = TextDocumentFormatter.WrapDocument("one\r\ntwo", width: 40);

        Assert.Equal(["one", "two"], lines);
    }

    // ── A line that fits is not reflowed (TUI-0005, found via system-monitor) ──

    /// <summary>
    /// A line that already fits comes back exactly as it was written.
    /// </summary>
    /// <remarks>
    /// Wrapping splits on whitespace and rejoins with single spaces, which is what wrapping
    /// means — and it was doing it to lines that never needed wrapping, so a block of
    /// aligned columns came back reflowed. <c>examples/system-monitor.tosh</c> drew
    /// <c>Used 42.8 GB</c> where its source says <c>  Used       42.8 GB</c>.
    /// </remarks>
    [Theory]
    [InlineData("  Used       42.8 GB")]
    [InlineData("a    b    c")]
    [InlineData("   leading and trailing   ")]
    [InlineData("single")]
    public void A_line_that_fits_keeps_its_spacing(string line)
        => Assert.Equal([line], TextDocumentFormatter.WrapParagraph(line, 80));

    /// <summary>A block of columns survives the document wrapper.</summary>
    [Fact]
    public void A_block_of_columns_is_not_reflowed()
    {
        var block = "  Used       42.8 GB\n  Available  80.7 GB\n\n  Total      123.5 GB";

        Assert.Equal(
            ["  Used       42.8 GB", "  Available  80.7 GB", string.Empty, "  Total      123.5 GB"],
            TextDocumentFormatter.WrapDocument(block, 80));
    }

    /// <summary>A line that does not fit is still wrapped, which is the job.</summary>
    [Fact]
    public void A_line_that_does_not_fit_is_still_wrapped()
    {
        var wrapped = TextDocumentFormatter.WrapParagraph("alpha beta gamma delta", 11);

        Assert.True(wrapped.Count > 1, "a line longer than the width should have been wrapped");
        Assert.All(wrapped, line => Assert.True(line.Length <= 11, $"'{line}' is wider than 11"));
    }

    /// <summary>
    /// Fitting is measured in columns, not code units.
    /// </summary>
    /// <remarks>
    /// Three CJK characters are six columns and three code units, so counting the wrong one
    /// calls a line that overruns its pane a line that fits (<c>TUI-0005</c>).
    /// </remarks>
    [Fact]
    public void Fitting_is_measured_in_columns()
    {
        // Two words, seven columns, four code units. Counting units calls this a fit and
        // returns it whole; counting columns wraps it, which is the right answer.
        Assert.Equal(2, TextDocumentFormatter.WrapParagraph("\u65e5\u672c \u8a9e", 4).Count);

        // And where it genuinely fits it comes back whole.
        Assert.Equal(
            ["\u65e5\u672c \u8a9e"],
            TextDocumentFormatter.WrapParagraph("\u65e5\u672c \u8a9e", 7));
    }

    /// <summary>Text with its own line breaks still collapses, which is the contract.</summary>
    /// <remarks>
    /// The fast path above must not take this case: a paragraph's newlines are an artefact
    /// of how it was typed, and <c>WrapDocument</c> is the one that treats them as content.
    /// The first draft of the fast path returned short multi-line text unchanged and broke
    /// exactly that.
    /// </remarks>
    [Fact]
    public void Short_text_with_newlines_is_still_collapsed()
        => Assert.Equal(
            ["one two three"],
            TextDocumentFormatter.WrapParagraph("one two\nthree", width: 80));
}
