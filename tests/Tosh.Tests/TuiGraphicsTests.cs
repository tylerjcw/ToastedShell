using Tosh.Tui;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

/// <summary>
/// Handing the terminal real pixels, and taking them back (<c>TUI-0025</c>).
/// </summary>
public sealed class TuiGraphicsTests
{
    private static TuiPixels Red(int width = 2, int height = 2)
        => new(width, height, [.. Enumerable.Repeat<byte>(0, width * height * 3)
            .Select((_, index) => index % 3 == 0 ? (byte)255 : (byte)0)]);

    private static Func<string, string?> Env(params (string Name, string Value)[] pairs)
        => name => pairs.FirstOrDefault(pair => pair.Name == name).Value;

    [Theory]
    [InlineData("TERM", "xterm-ghostty")]
    [InlineData("TERM", "xterm-kitty")]
    [InlineData("TERM_PROGRAM", "WezTerm")]
    [InlineData("KITTY_WINDOW_ID", "3")]
    [InlineData("GHOSTTY_RESOURCES_DIR", "/usr/share/ghostty")]
    public void A_terminal_that_says_it_speaks_the_protocol_is_believed(string name, string value)
        => Assert.Equal(TuiGraphicsProtocol.Kitty, TuiGraphics.Detect(Env((name, value))));

    [Theory]
    [InlineData("TERM", "xterm-256color")]
    [InlineData("TERM", "linux")]
    [InlineData("TERM_PROGRAM", "Apple_Terminal")]
    public void Anything_else_gets_half_blocks(string name, string value)
        => Assert.Equal(TuiGraphicsProtocol.HalfBlocks, TuiGraphics.Detect(Env((name, value))));

    [Fact]
    public void A_multiplexer_gets_half_blocks_even_over_a_terminal_that_speaks_it()
    {
        // The multiplexer does not know the picture was sent, so it would be drawn over
        // whatever pane it decided to put there.
        Assert.Equal(
            TuiGraphicsProtocol.HalfBlocks,
            TuiGraphics.Detect(Env(("TERM", "xterm-ghostty"), ("TMUX", "/tmp/tmux-1000/default"))));

        Assert.Equal(
            TuiGraphicsProtocol.HalfBlocks,
            TuiGraphics.Detect(Env(("TERM", "screen.xterm-kitty"))));
    }

    [Theory]
    [InlineData("kitty", TuiGraphicsProtocol.Kitty)]
    [InlineData("half", TuiGraphicsProtocol.HalfBlocks)]
    [InlineData("off", TuiGraphicsProtocol.HalfBlocks)]
    public void A_reader_whose_terminal_lies_has_the_last_word(string told, TuiGraphicsProtocol expected)
        => Assert.Equal(
            expected,
            TuiGraphics.Detect(Env(("TERM", "xterm-256color"), ("TOSH_TUI_GRAPHICS", told))));

    [Fact]
    public void A_picture_is_sent_as_one_apc_string_per_chunk()
    {
        var sent = TuiGraphics.Transmit(new TuiPlacement(7, 3, 4, 10, 5, Red()));

        // Moved to where it goes first, because a placement lands at the cursor.
        Assert.StartsWith("\x1b[5;4H", sent, StringComparison.Ordinal);

        Assert.Contains("a=T,f=24,i=7,s=2,v=2,c=10,r=5,q=2,", sent, StringComparison.Ordinal);
        Assert.Contains("m=0;", sent, StringComparison.Ordinal);
        Assert.EndsWith("\x1b\\", sent, StringComparison.Ordinal);
    }

    [Fact]
    public void A_big_picture_is_chunked_and_only_the_last_chunk_says_so()
    {
        // 4096 base64 characters is the protocol's own limit, so a picture past it has to
        // arrive in pieces with every piece but the last marked as "more follows".
        var sent = TuiGraphics.Transmit(new TuiPlacement(1, 0, 0, 40, 20, Red(64, 64)));

        Assert.True(CountOf(sent, "m=1;") >= 1, "A picture over the chunk limit was sent whole.");
        Assert.Equal(1, CountOf(sent, "m=0;"));
        Assert.Equal(1, CountOf(sent, "a=T,"));

        static int CountOf(string text, string needle)
        {
            var count = 0;

            for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0;
                 at = text.IndexOf(needle, at + 1, StringComparison.Ordinal))
            {
                count += 1;
            }

            return count;
        }
    }

    [Fact]
    public void A_picture_that_is_still_there_is_not_sent_again()
    {
        // A 4K frame down the wire every time a clock ticks is a slideshow, not a preview.
        var pixels = Red();
        var first = Frame(pixels);
        var second = Frame(pixels);

        Assert.Contains("a=T", TuiTerminalWriter.Present(first), StringComparison.Ordinal);
        Assert.DoesNotContain("a=T", TuiTerminalWriter.Present(first, second), StringComparison.Ordinal);

        static TuiBuffer Frame(TuiPixels pixels)
        {
            var buffer = new TuiBuffer(new TuiSize(10, 4));

            buffer.Place(new TuiPlacement(1, 0, 0, 4, 2, pixels));
            return buffer;
        }
    }

    [Fact]
    public void A_picture_that_has_gone_is_taken_back()
    {
        var previous = new TuiBuffer(new TuiSize(10, 4));

        previous.Place(new TuiPlacement(1, 0, 0, 4, 2, Red()));

        var sent = TuiTerminalWriter.Present(previous, new TuiBuffer(new TuiSize(10, 4)));

        Assert.Contains("a=d,d=i,i=1", sent, StringComparison.Ordinal);
    }

    [Fact]
    public void A_picture_that_moved_is_taken_back_and_sent_again()
    {
        var pixels = Red();
        var previous = new TuiBuffer(new TuiSize(10, 4));
        var next = new TuiBuffer(new TuiSize(10, 4));

        previous.Place(new TuiPlacement(1, 0, 0, 4, 2, pixels));
        next.Place(new TuiPlacement(1, 2, 1, 4, 2, pixels));

        var sent = TuiTerminalWriter.Present(previous, next);

        Assert.Contains("a=T", sent, StringComparison.Ordinal);
    }

    [Fact]
    public void An_image_widget_places_pixels_when_the_terminal_speaks_the_protocol()
    {
        var was = TuiImage.Protocol;

        try
        {
            TuiImage.Protocol = TuiGraphicsProtocol.Kitty;

            var buffer = Render(new TuiImage(Red(4, 4)) { Fit = TuiImageFit.Stretch }, 6, 3);

            var placement = Assert.Single(buffer.Placements);

            Assert.Equal(6, placement.Columns);
            Assert.Equal(3, placement.Rows);

            // And the cells underneath are blank: a terminal draws the picture over the
            // text, so anything left there shows through and outlives the picture.
            Assert.Equal("   ", buffer.RowText(0)[..3]);
        }
        finally
        {
            TuiImage.Protocol = was;
        }
    }

    [Fact]
    public void And_draws_half_blocks_when_it_does_not()
    {
        var was = TuiImage.Protocol;

        try
        {
            TuiImage.Protocol = TuiGraphicsProtocol.HalfBlocks;

            var buffer = Render(new TuiImage(Red(4, 4)) { Fit = TuiImageFit.Stretch }, 6, 3);

            Assert.Empty(buffer.Placements);
            Assert.Contains('▀', buffer.RowText(0));
        }
        finally
        {
            TuiImage.Protocol = was;
        }
    }

    [Fact]
    public void A_picture_half_off_its_pane_asks_for_half_a_picture()
    {
        // Not for a whole one: the terminal would happily draw it over the pane beside it.
        var buffer = new TuiBuffer(new TuiSize(10, 4));
        var surface = new TuiSurface(buffer, new TuiRect(0, 0, 4, 4));

        var placed = surface.Place(1, new TuiRect(2, 0, 6, 4), Red(8, 8));

        Assert.Equal(2, placed.Width);

        var placement = Assert.Single(buffer.Placements);

        Assert.Equal(2, placement.Columns);
        Assert.True(placement.Pixels.Width < 8, "The whole picture was sent for a clipped placement.");
    }

    private static TuiBuffer Render(TuiWidget widget, int width, int height)
    {
        var buffer = new TuiBuffer(new TuiSize(width, height));
        var bounds = new TuiRect(0, 0, width, height);

        widget.Measure(TuiConstraints.From(new TuiSize(width, height)));
        widget.Arrange(bounds);
        widget.Paint(new TuiSurface(buffer, bounds));

        return buffer;
    }
}
