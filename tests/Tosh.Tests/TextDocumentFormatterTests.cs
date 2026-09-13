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
}
