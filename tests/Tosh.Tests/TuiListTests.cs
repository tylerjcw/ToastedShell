using System.Dynamic;
using Tosh.Tui;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

/// <summary>The list widget: choosing, ticking, and rendering whatever it was given.</summary>
public sealed class TuiListTests
{
    private static string[] Render(TuiWidget widget, int width, int height)
    {
        var buffer = new TuiBuffer(new TuiSize(width, height));
        var bounds = new TuiRect(0, 0, width, height);

        widget.Measure(TuiConstraints.From(new TuiSize(width, height)));
        widget.Arrange(bounds);
        widget.Draw(new TuiSurface(buffer, bounds));

        return Enumerable.Range(0, height).Select(row => buffer.RowText(row).TrimEnd()).ToArray();
    }

    private static TuiList Focused(TuiList list)
    {
        new TuiFocus(list).Focus(list);
        return list;
    }

    private static TuiInputEvent Key(ConsoleKey key)
        => TuiInputEvent.FromKey(new ConsoleKeyInfo('\0', key, false, false, false));

    [Fact]
    public void The_first_item_starts_selected_and_is_marked()
    {
        var list = new TuiList(["alpha", "beta"]);

        Assert.Equal(["> alpha", "  beta"], Render(list, 20, 2));
        Assert.Equal("alpha", list.SelectedItem);
    }

    [Fact]
    public void Arrows_move_the_selection_and_stop_at_the_ends()
    {
        var list = Focused(new TuiList(["a", "b", "c"]));

        list.OnInput(Key(ConsoleKey.DownArrow));
        Assert.Equal(1, list.SelectedIndex);

        list.OnInput(Key(ConsoleKey.UpArrow));
        list.OnInput(Key(ConsoleKey.UpArrow));
        Assert.Equal(0, list.SelectedIndex);

        list.OnInput(Key(ConsoleKey.End));
        Assert.Equal(2, list.SelectedIndex);

        list.OnInput(Key(ConsoleKey.DownArrow));
        Assert.Equal(2, list.SelectedIndex);
    }

    [Fact]
    public void An_unfocused_list_ignores_the_keyboard()
    {
        var list = new TuiList(["a", "b"]);

        Assert.False(list.OnInput(Key(ConsoleKey.DownArrow)));
        Assert.Equal(0, list.SelectedIndex);
    }

    [Fact]
    public void Enter_activates_the_selected_item()
    {
        object? activated = null;
        var list = Focused(new TuiList(["a", "b"]) { Activated = item => activated = item });

        list.OnInput(Key(ConsoleKey.DownArrow));
        list.OnInput(Key(ConsoleKey.Enter));

        Assert.Equal("b", activated);
    }

    [Fact]
    public void Replacing_the_items_keeps_the_selection_where_it_was()
    {
        var list = Focused(new TuiList(["a", "b", "c", "d"]));
        list.OnInput(Key(ConsoleKey.DownArrow));
        list.OnInput(Key(ConsoleKey.DownArrow));

        list.Items = ["w", "x", "y", "z"];

        // A list refreshed underneath a reader should not jump back to the top.
        Assert.Equal(2, list.SelectedIndex);
        Assert.Equal("y", list.SelectedItem);
    }

    [Fact]
    public void Replacing_with_fewer_items_clamps_rather_than_pointing_past_the_end()
    {
        var list = Focused(new TuiList(["a", "b", "c", "d"]));
        list.OnInput(Key(ConsoleKey.End));

        list.Items = ["only"];

        Assert.Equal(0, list.SelectedIndex);
        Assert.Equal("only", list.SelectedItem);
    }

    [Fact]
    public void An_empty_list_has_no_selected_item()
    {
        var list = new TuiList();

        Assert.Null(list.SelectedItem);
        Assert.Equal([""], Render(list, 10, 1));
    }

    [Fact]
    public void Multi_select_ticks_items_with_space()
    {
        var list = Focused(new TuiList(["a", "b", "c"]) { MultiSelect = true });

        list.OnInput(Key(ConsoleKey.Spacebar));
        list.OnInput(Key(ConsoleKey.DownArrow));
        list.OnInput(Key(ConsoleKey.DownArrow));
        list.OnInput(Key(ConsoleKey.Spacebar));

        Assert.Equal(["a", "c"], list.CheckedItems);
        Assert.Equal(["  [x] a", "  [ ] b", "> [x] c"], Render(list, 20, 3));
    }

    [Fact]
    public void Ticking_twice_unticks()
    {
        var list = Focused(new TuiList(["a"]) { MultiSelect = true });

        list.OnInput(Key(ConsoleKey.Spacebar));
        list.OnInput(Key(ConsoleKey.Spacebar));

        Assert.Empty(list.CheckedItems);
    }

    [Fact]
    public void A_display_property_works_on_a_clr_object()
    {
        var list = new TuiList([new Version(1, 2), new Version(3, 4)]) { DisplayProperty = "Major" };

        Assert.Equal(["> 1", "  3"], Render(list, 10, 2));
    }

    [Fact]
    public void A_display_property_works_on_a_script_record()
    {
        // Records and dictionaries are what a script actually has, and their fields are
        // not CLR properties — reflection alone renders a column of type names.
        dynamic first = new ExpandoObject();
        first.Name = "alpha";
        dynamic second = new ExpandoObject();
        second.Name = "beta";

        var list = new TuiList([first, second]) { DisplayProperty = "Name" };

        Assert.Equal(["> alpha", "  beta"], Render(list, 20, 2));
    }

    [Fact]
    public void A_display_selector_takes_precedence_over_a_property()
    {
        var list = new TuiList([1, 2]) { DisplayProperty = "Major", DisplaySelector = value => $"#{value}" };

        Assert.Equal(["> #1", "  #2"], Render(list, 10, 2));
    }

    [Fact]
    public void A_missing_display_property_falls_back_to_the_value()
    {
        var list = new TuiList(["plain"]) { DisplayProperty = "NoSuchThing" };

        Assert.Equal(["> plain"], Render(list, 10, 1));
    }

    [Fact]
    public void Clicking_a_row_selects_it()
    {
        var list = new TuiList(["a", "b", "c"]);
        list.Arrange(new TuiRect(0, 0, 10, 3));

        var click = TuiInputEvent.FromMouse(
            new TuiMouseEvent(TuiMouseAction.Press, TuiMouseButton.Left, 3, 2, false, false, false));

        Assert.True(list.OnInput(click));
        Assert.Equal(2, list.SelectedIndex);
    }

    [Fact]
    public void A_list_keeps_its_selection_in_view_without_being_wrapped()
    {
        var list = Focused(new TuiList(
            Enumerable.Range(1, 40).Select(number => (object?)$"item{number}").ToArray()));

        list.Measure(TuiConstraints.From(new TuiSize(20, 5)));
        list.Arrange(new TuiRect(0, 0, 20, 5));

        for (var press = 0; press < 10; press += 1)
        {
            list.OnInput(Key(ConsoleKey.DownArrow));
        }

        Assert.Equal(10, list.SelectedIndex);

        // The selected row is inside the window, not below it.
        Assert.InRange(list.SelectedIndex, list.Offset, list.Offset + 4);

        var rows = Render(list, 20, 5);
        Assert.Contains(rows, row => row.Contains("> item11", StringComparison.Ordinal));
    }

    [Fact]
    public void A_click_lands_on_the_row_it_looks_like_even_when_scrolled()
    {
        var list = Focused(new TuiList(
            Enumerable.Range(1, 40).Select(number => (object?)$"item{number}").ToArray()));

        list.Measure(TuiConstraints.From(new TuiSize(20, 5)));
        list.Arrange(new TuiRect(0, 0, 20, 5));
        list.OnInput(Key(ConsoleKey.End));

        var click = TuiInputEvent.FromMouse(
            new TuiMouseEvent(TuiMouseAction.Press, TuiMouseButton.Left, 3, 0, false, false, false));

        list.OnInput(click);

        // Row zero of a list scrolled to the end is not item one.
        Assert.Equal(list.Offset, list.SelectedIndex);
        Assert.Equal("item36", list.SelectedItem);
    }

    [Fact]
    public void Selection_changes_are_reported()
    {
        var seen = new List<int>();
        var list = Focused(new TuiList(["a", "b", "c"]) { SelectionChanged = index => seen.Add(index) });

        list.OnInput(Key(ConsoleKey.DownArrow));
        list.OnInput(Key(ConsoleKey.DownArrow));
        list.OnInput(Key(ConsoleKey.DownArrow));

        // Three presses, two moves: the third had nowhere to go.
        Assert.Equal([1, 2], seen);
    }
}
