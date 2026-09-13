using Tosh.Tui;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

/// <summary>A document of styled lines that owns its own offset.</summary>
public sealed class TuiLinesTests
{
    private static TuiBuffer Paint(TuiWidget widget, int width, int height)
    {
        var buffer = new TuiBuffer(new TuiSize(width, height));
        var bounds = new TuiRect(0, 0, width, height);

        widget.Measure(TuiConstraints.From(new TuiSize(width, height)));
        widget.Arrange(bounds);
        widget.Draw(new TuiSurface(buffer, bounds));

        return buffer;
    }

    private static string[] Render(TuiWidget widget, int width, int height)
    {
        var buffer = Paint(widget, width, height);
        return [.. Enumerable.Range(0, height).Select(row => buffer.RowText(row).TrimEnd())];
    }

    private static TuiLines Focused(TuiLines lines)
    {
        new TuiFocus(lines).Focus(lines);
        return lines;
    }

    private static TuiSpanLine Line(string text) => new([new TuiSpan(text)]);

    private static TuiInputEvent Key(ConsoleKey key)
        => TuiInputEvent.FromKey(new ConsoleKeyInfo('\0', key, false, false, false));

    [Fact]
    public void A_row_is_drawn_from_its_runs()
    {
        var lines = new TuiLines([
            new TuiSpanLine()
                .Add("› ", new TuiStyle("cyan"))
                .Add("grep", new TuiStyle(Attributes: TuiTextAttributes.Bold))
                .Add("  [BuiltIn]", new TuiStyle(Attributes: TuiTextAttributes.Dim)),
        ]);

        var buffer = Paint(lines, 30, 1);

        Assert.Equal("› grep  [BuiltIn]", buffer.RowText(0).TrimEnd());

        // Each run keeps its own style, which is the whole reason a row is runs.
        Assert.Equal("cyan", buffer[0, 0].Style.Foreground);
        Assert.Equal(TuiTextAttributes.Bold, buffer[2, 0].Style.Attributes);
        Assert.Equal(TuiTextAttributes.Dim, buffer[8, 0].Style.Attributes);
    }

    [Fact]
    public void Only_the_visible_rows_are_drawn()
    {
        var lines = new TuiLines(Enumerable.Range(1, 100).Select(n => Line($"line{n}")));

        Assert.Equal(["line1", "line2", "line3"], Render(lines, 20, 3));
    }

    [Fact]
    public void It_scrolls_itself()
    {
        var lines = Focused(new TuiLines(Enumerable.Range(1, 20).Select(n => Line($"line{n}"))));

        lines.Measure(TuiConstraints.From(new TuiSize(20, 5)));
        lines.Arrange(new TuiRect(0, 0, 20, 5));

        lines.OnInput(Key(ConsoleKey.DownArrow));
        lines.OnInput(Key(ConsoleKey.DownArrow));

        Assert.Equal(2, lines.Offset);
        Assert.Equal(["line3", "line4", "line5", "line6", "line7"], Render(lines, 20, 5));

        lines.OnInput(Key(ConsoleKey.End));
        Assert.Equal(15, lines.Offset);

        lines.OnInput(Key(ConsoleKey.Home));
        Assert.Equal(0, lines.Offset);
    }

    [Fact]
    public void The_offset_stops_at_the_ends()
    {
        var lines = Focused(new TuiLines(Enumerable.Range(1, 6).Select(n => Line($"line{n}"))));
        lines.Arrange(new TuiRect(0, 0, 20, 4));

        lines.OnInput(Key(ConsoleKey.UpArrow));
        Assert.Equal(0, lines.Offset);

        for (var press = 0; press < 20; press += 1)
        {
            lines.OnInput(Key(ConsoleKey.DownArrow));
        }

        // Two rows past the window, so the last line sits at the bottom and no further.
        Assert.Equal(2, lines.Offset);
    }

    [Fact]
    public void Content_replaced_underneath_a_reader_keeps_its_place()
    {
        var lines = Focused(new TuiLines(Enumerable.Range(1, 40).Select(n => Line($"a{n}"))));
        lines.Arrange(new TuiRect(0, 0, 20, 5));
        lines.OnInput(Key(ConsoleKey.PageDown));

        var was = lines.Offset;
        lines.Lines = [.. Enumerable.Range(1, 40).Select(n => Line($"b{n}"))];

        Assert.Equal(was, lines.Offset);
    }

    [Fact]
    public void Content_that_got_shorter_does_not_leave_the_offset_past_the_end()
    {
        var lines = Focused(new TuiLines(Enumerable.Range(1, 40).Select(n => Line($"a{n}"))));
        lines.Arrange(new TuiRect(0, 0, 20, 5));
        lines.OnInput(Key(ConsoleKey.End));

        lines.Lines = [Line("only")];

        Assert.Equal(0, lines.Offset);
        Assert.Equal(["only", "", "", "", ""], Render(lines, 20, 5));
    }

    [Fact]
    public void The_wheel_scrolls_it_without_the_keyboard()
    {
        var lines = new TuiLines(Enumerable.Range(1, 20).Select(n => Line($"line{n}")));
        lines.Arrange(new TuiRect(0, 0, 20, 5));

        lines.OnInput(TuiInputEvent.FromMouse(
            new TuiMouseEvent(TuiMouseAction.Scroll, TuiMouseButton.ScrollDown, 2, 2, false, false, false)));

        Assert.Equal(1, lines.Offset);
    }

    [Fact]
    public void A_scrollbar_says_where_in_the_document_you_are()
    {
        var lines = Focused(new TuiLines(Enumerable.Range(1, 40).Select(n => Line($"line{n}")))
        {
            Scrollbar = true,
        });

        lines.Arrange(new TuiRect(0, 0, 12, 4));

        var top = Render(lines, 12, 4);
        Assert.All(top, row => Assert.Equal(12, row.Length));
        Assert.Equal('█', top[0][^1]);
        Assert.Equal('│', top[3][^1]);

        lines.OnInput(Key(ConsoleKey.End));

        var bottom = Render(lines, 12, 4);
        Assert.Equal('│', bottom[0][^1]);
        Assert.Equal('█', bottom[3][^1]);
    }

    [Fact]
    public void Content_that_fits_gets_no_scrollbar_to_lie_about()
    {
        var lines = new TuiLines([Line("one"), Line("two")]) { Scrollbar = true };

        Assert.Equal(["one", "two", "", ""], Render(lines, 12, 4));
    }

    [Fact]
    public void A_scrollbar_takes_a_column_from_the_content_rather_than_covering_it()
    {
        var lines = new TuiLines(Enumerable.Range(1, 20).Select(n => Line("xxxxxxxxxxxx")))
        {
            Scrollbar = true,
        };

        var rows = Render(lines, 12, 3);

        Assert.Equal("xxxxxxxxxxx█", rows[0]);
    }

    [Fact]
    public void Plain_text_can_be_set_directly()
    {
        var lines = new TuiLines { Text = "one\ntwo\nthree" };

        Assert.Equal(["one", "two", "three"], Render(lines, 10, 3));
        Assert.Equal("one\ntwo\nthree", lines.Value);
    }
}
