using Tosh.Tui;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

/// <summary>Scrolling as a container rather than a field on every widget.</summary>
public sealed class TuiScrollTests
{
    private static string Lines(int count)
        => string.Join('\n', Enumerable.Range(1, count).Select(number => $"line{number}"));

    private static (TuiScroll Scroll, Func<string[]> Render) Build(int contentLines, int width, int height)
    {
        var scroll = new TuiScroll(new TuiTextWidget(Lines(contentLines)) { Wrap = false });
        var bounds = new TuiRect(0, 0, width, height);

        scroll.Measure(TuiConstraints.From(new TuiSize(width, height)));
        scroll.Arrange(bounds);

        return (scroll, () =>
        {
            var buffer = new TuiBuffer(new TuiSize(width, height));
            scroll.Draw(new TuiSurface(buffer, bounds));
            return Enumerable.Range(0, height).Select(row => buffer.RowText(row).TrimEnd()).ToArray();
        });
    }

    private static TuiInputEvent Key(ConsoleKey key)
        => TuiInputEvent.FromKey(new ConsoleKeyInfo('\0', key, false, false, false));

    [Fact]
    public void A_child_shorter_than_the_viewport_does_not_scroll()
    {
        var (scroll, render) = Build(contentLines: 2, width: 10, height: 5);

        Assert.Equal(0, scroll.MaxOffset);
        Assert.Equal(["line1", "line2", "", "", ""], render());
    }

    [Fact]
    public void A_taller_child_shows_a_window_onto_itself()
    {
        var (_, render) = Build(contentLines: 20, width: 10, height: 3);

        Assert.Equal(["line1", "line2", "line3"], render());
    }

    [Fact]
    public void Scrolling_moves_the_window()
    {
        var (scroll, render) = Build(contentLines: 20, width: 10, height: 3);

        scroll.ScrollBy(4);

        Assert.Equal(["line5", "line6", "line7"], render());
    }

    [Fact]
    public void Scrolling_stops_at_the_ends()
    {
        var (scroll, render) = Build(contentLines: 6, width: 10, height: 3);

        scroll.ScrollBy(-5);
        Assert.Equal(0, scroll.Offset);

        scroll.ScrollBy(500);
        Assert.Equal(3, scroll.Offset);
        Assert.Equal(["line4", "line5", "line6"], render());
    }

    [Fact]
    public void Scrolling_into_view_reaches_a_row_below_the_window()
    {
        var (scroll, render) = Build(contentLines: 20, width: 10, height: 3);

        scroll.ScrollIntoView(9);

        // The requested row is the last visible one, not the first: it came from below.
        Assert.Equal(["line8", "line9", "line10"], render());
    }

    [Fact]
    public void Scrolling_into_view_reaches_a_row_above_the_window()
    {
        var (scroll, render) = Build(contentLines: 20, width: 10, height: 3);
        scroll.ScrollBy(10);

        scroll.ScrollIntoView(2);

        Assert.Equal(["line3", "line4", "line5"], render());
    }

    [Fact]
    public void Scrolling_into_view_of_something_already_visible_does_nothing()
    {
        var (scroll, _) = Build(contentLines: 20, width: 10, height: 5);
        scroll.ScrollBy(4);

        scroll.ScrollIntoView(6);

        Assert.Equal(4, scroll.Offset);
    }

    [Fact]
    public void The_keyboard_scrolls_only_while_focused()
    {
        var (scroll, _) = Build(contentLines: 20, width: 10, height: 3);

        Assert.False(scroll.OnInput(Key(ConsoleKey.DownArrow)));
        Assert.Equal(0, scroll.Offset);

        new TuiFocus(scroll).Focus(scroll);

        Assert.True(scroll.OnInput(Key(ConsoleKey.DownArrow)));
        Assert.Equal(1, scroll.Offset);
    }

    [Fact]
    public void Page_keys_move_by_nearly_a_screen()
    {
        var (scroll, _) = Build(contentLines: 50, width: 10, height: 10);
        new TuiFocus(scroll).Focus(scroll);

        scroll.OnInput(Key(ConsoleKey.PageDown));

        // One row of overlap, so the reader keeps their place.
        Assert.Equal(9, scroll.Offset);

        scroll.OnInput(Key(ConsoleKey.End));
        Assert.Equal(40, scroll.Offset);

        scroll.OnInput(Key(ConsoleKey.Home));
        Assert.Equal(0, scroll.Offset);
    }

    [Fact]
    public void The_wheel_scrolls_what_it_is_over_without_focusing_it()
    {
        var (scroll, _) = Build(contentLines: 20, width: 10, height: 3);

        var wheel = TuiInputEvent.FromMouse(
            new TuiMouseEvent(TuiMouseAction.Scroll, TuiMouseButton.ScrollDown, 2, 1, false, false, false));

        Assert.True(scroll.OnInput(wheel));
        Assert.Equal(3, scroll.Offset);
    }

    [Fact]
    public void The_wheel_outside_the_viewport_is_not_ours()
    {
        var (scroll, _) = Build(contentLines: 20, width: 10, height: 3);

        var wheel = TuiInputEvent.FromMouse(
            new TuiMouseEvent(TuiMouseAction.Scroll, TuiMouseButton.ScrollDown, 40, 40, false, false, false));

        Assert.False(scroll.OnInput(wheel));
        Assert.Equal(0, scroll.Offset);
    }

    [Fact]
    public void A_scrolled_child_still_cannot_draw_outside_the_viewport()
    {
        var buffer = new TuiBuffer(new TuiSize(10, 5));
        var scroll = new TuiScroll(new TuiTextWidget(Lines(20)) { Wrap = false });
        var bounds = new TuiRect(0, 2, 10, 2);

        scroll.Measure(TuiConstraints.From(new TuiSize(10, 2)));
        scroll.Arrange(bounds);
        scroll.ScrollBy(5);
        scroll.Draw(new TuiSurface(buffer, bounds));

        // Only the two rows it was given, and nothing above or below them.
        Assert.Equal("          ", buffer.RowText(0));
        Assert.Equal("          ", buffer.RowText(1));
        Assert.Equal("line6     ", buffer.RowText(2));
        Assert.Equal("line7     ", buffer.RowText(3));
        Assert.Equal("          ", buffer.RowText(4));
    }
}
