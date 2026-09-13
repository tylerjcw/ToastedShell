using Tosh.Tui;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

/// <summary>
/// A hidden widget takes no room, and the ones beside it close up.
/// </summary>
/// <remarks>
/// This is how a screen holds several dialogs and shows one: each is gated by
/// <c>IsVisible</c> (from markup, by <c>When</c>), and the container must lay the tree out
/// as though the hidden ones were not written at all.
/// </remarks>
public sealed class TuiVisibilityTests
{
    private static TuiTextWidget Text(string text, string size, bool visible = true)
        => new(text) { Size = TuiLength.Parse(size), IsVisible = visible };

    private static void Lay(TuiWidget widget, int width, int height)
    {
        widget.Measure(TuiConstraints.From(new TuiSize(width, height)));
        widget.Arrange(new TuiRect(0, 0, width, height));
    }

    private static string[] Render(TuiWidget widget, int width, int height)
    {
        var buffer = new TuiBuffer(new TuiSize(width, height));
        var bounds = new TuiRect(0, 0, width, height);

        Lay(widget, width, height);
        widget.Paint(new TuiSurface(buffer, bounds));

        return [.. Enumerable.Range(0, height).Select(row => buffer.RowText(row).TrimEnd())];
    }

    [Fact]
    public void A_hidden_child_gives_its_room_to_the_ones_that_are_shown()
    {
        var shown = Text("shown", "*");
        var stack = new TuiStack(TuiOrientation.Vertical)
        {
            Items = [Text("hidden", "*", visible: false), shown],
        };

        Lay(stack, 20, 6);

        Assert.Equal(6, shown.Bounds.Height);
        Assert.Equal(0, shown.Bounds.Top);
    }

    /// <summary>
    /// The bug this file was opened for: three dialogs in a column, the third one up.
    /// </summary>
    /// <remarks>
    /// The hidden children were left out of the star weight but still paid a share, so the
    /// total handed out ran past what there was to give and the rounding absorber took the
    /// overspend back off the last child — which was the only one being shown.
    /// </remarks>
    [Fact]
    public void The_last_child_is_shown_when_it_is_the_only_one_shown()
    {
        var third = Text("third", "*");
        var stack = new TuiStack(TuiOrientation.Vertical)
        {
            Items =
            [
                Text("first", "*", visible: false),
                Text("second", "*", visible: false),
                third,
            ],
        };

        Assert.Equal(["third"], Render(stack, 20, 3).Where(line => line.Length > 0));
        Assert.Equal(3, third.Bounds.Height);
    }

    [Fact]
    public void A_hidden_child_with_a_fixed_size_does_not_ask_for_it()
    {
        var stack = new TuiStack(TuiOrientation.Vertical)
        {
            Items = [Text("hidden", "4", visible: false), Text("shown", "auto")],
        };

        Assert.Equal(1, stack.Measure(TuiConstraints.From(new TuiSize(20, 10))).Height);
    }

    [Fact]
    public void A_gap_is_not_left_where_a_hidden_child_would_have_been()
    {
        var stack = new TuiStack(TuiOrientation.Vertical)
        {
            Gap = 1,
            Items = [Text("one", "auto"), Text("hidden", "auto", visible: false), Text("two", "auto")],
        };

        Assert.Equal(["one", "", "two"], Render(stack, 20, 3));
    }

    [Fact]
    public void Weight_is_shared_between_the_children_that_are_shown()
    {
        var left = Text("left", "*");
        var right = Text("right", "2*");
        var stack = new TuiStack(TuiOrientation.Horizontal)
        {
            Items = [left, Text("gone", "3*", visible: false), right],
        };

        Lay(stack, 12, 1);

        Assert.Equal(4, left.Bounds.Width);
        Assert.Equal(8, right.Bounds.Width);
        Assert.Equal(4, right.Bounds.Left);
    }
}
