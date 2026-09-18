using Tosh.Tui;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

/// <summary>
/// Edges first, and whatever is left over to the middle (<c>TUI-0002</c>).
/// </summary>
/// <remarks>
/// The shape almost every full-screen application has. A stack can do it only by nesting — a
/// column holding a header, a row holding a sidebar and a body, and a footer — which is three
/// containers describing one idea, and getting the nesting order wrong is what decides
/// whether the sidebar runs full height.
/// </remarks>
public sealed class TuiDockTests
{
    private static void Layout(TuiWidget widget, int width, int height)
    {
        widget.Measure(new TuiConstraints(width, height));
        widget.Arrange(new TuiRect(0, 0, width, height));
    }

    [Fact]
    public void Edges_take_what_they_need_and_the_fill_takes_the_rest()
    {
        var header = new TuiTextWidget("HEADER");
        var footer = new TuiTextWidget("status");
        var body = new TuiTextWidget("body");

        var dock = new TuiDock()
            .Add(header, TuiDockSide.Top)
            .Add(footer, TuiDockSide.Bottom)
            .Add(body, TuiDockSide.Fill);

        Layout(dock, 40, 10);

        Assert.Equal(new TuiRect(0, 0, 40, 1), header.Bounds);
        Assert.Equal(new TuiRect(0, 9, 40, 1), footer.Bounds);
        Assert.Equal(new TuiRect(0, 1, 40, 8), body.Bounds);
    }

    /// <summary>
    /// Order decides the corners, which is the whole of the semantics.
    /// </summary>
    /// <remarks>
    /// Docked top first, the header runs the full width and the sidebar starts below it.
    /// Written the other way round, the sidebar runs full height and the header starts
    /// beside it. That is not an accident to work around — it is how you say which of the
    /// two owns the corner.
    /// </remarks>
    [Fact]
    public void Which_edge_owns_the_corner_is_the_order_they_were_docked()
    {
        var topFirstHeader = new TuiTextWidget("TOP");
        var topFirstSide = new TuiTextWidget("LEFT");

        Layout(
            new TuiDock()
                .Add(topFirstHeader, TuiDockSide.Top)
                .Add(topFirstSide, TuiDockSide.Left)
                .Add(new TuiTextWidget("rest"), TuiDockSide.Fill),
            30,
            6);

        Assert.Equal(30, topFirstHeader.Bounds.Width);
        Assert.Equal(1, topFirstSide.Bounds.Top);

        var sideFirstHeader = new TuiTextWidget("TOP");
        var sideFirstSide = new TuiTextWidget("LEFT");

        Layout(
            new TuiDock()
                .Add(sideFirstSide, TuiDockSide.Left)
                .Add(sideFirstHeader, TuiDockSide.Top)
                .Add(new TuiTextWidget("rest"), TuiDockSide.Fill),
            30,
            6);

        Assert.Equal(6, sideFirstSide.Bounds.Height);
        Assert.Equal(0, sideFirstHeader.Bounds.Top);
        Assert.True(
            sideFirstHeader.Bounds.Left > 0,
            "the header should have started beside the sidebar that claimed the corner");
    }

    [Fact]
    public void All_four_edges_can_be_docked()
    {
        var top = new TuiTextWidget("t");
        var bottom = new TuiTextWidget("b");
        var left = new TuiTextWidget("l");
        var right = new TuiTextWidget("r");
        var middle = new TuiTextWidget("m");

        Layout(
            new TuiDock()
                .Add(top, TuiDockSide.Top)
                .Add(bottom, TuiDockSide.Bottom)
                .Add(left, TuiDockSide.Left)
                .Add(right, TuiDockSide.Right)
                .Add(middle, TuiDockSide.Fill),
            20,
            8);

        Assert.Equal(0, top.Bounds.Top);
        Assert.Equal(7, bottom.Bounds.Top);
        Assert.Equal(0, left.Bounds.Left);
        Assert.Equal(19, right.Bounds.Left);
        Assert.Equal(new TuiRect(1, 1, 18, 6), middle.Bounds);
    }

    /// <summary>
    /// The fill child is measured before it is arranged, like every other child.
    /// </summary>
    /// <remarks>
    /// The edges are measured on the way past, to find out how much they take. The filler is
    /// not, and the first version arranged it without ever measuring it — so a child that
    /// works out its own layout during measure, as a grid does with its tracks, was arranged
    /// against numbers it had never computed and drew nothing at all.
    /// </remarks>
    [Fact]
    public void The_fill_child_is_measured_before_it_is_arranged()
    {
        var grid = new TuiGrid("auto, *");

        grid.Add(new TuiTextWidget("label"), 0, 0);
        grid.Add(new TuiTextWidget("value"), 0, 1);

        var dock = new TuiDock()
            .Add(new TuiTextWidget("HEADER"), TuiDockSide.Top)
            .Add(grid, TuiDockSide.Fill);

        var buffer = new TuiBuffer(new TuiSize(30, 4));

        Layout(dock, 30, 4);
        dock.Draw(new TuiSurface(buffer, new TuiRect(0, 0, 30, 4)));

        Assert.Equal("label value", buffer.RowText(1).TrimEnd());
    }

    /// <summary>With nothing marked Fill, the last child takes the rest.</summary>
    /// <remarks>
    /// Which is what every other container does with its last child. A dock holding only a
    /// header should fill the screen with it rather than leave the screen blank.
    /// </remarks>
    [Fact]
    public void Without_a_fill_the_last_child_takes_what_is_left()
    {
        var header = new TuiTextWidget("HEADER");
        var body = new TuiTextWidget("body");

        Layout(new TuiDock().Add(header, TuiDockSide.Top).Add(body, TuiDockSide.Left), 20, 6);

        Assert.Equal(1, header.Bounds.Height);
        Assert.Equal(new TuiRect(0, 1, 20, 5), body.Bounds);
    }

    /// <summary>A hidden child takes no room, so the rest close up.</summary>
    [Fact]
    public void A_hidden_child_takes_no_room()
    {
        var header = new TuiTextWidget("HEADER") { IsVisible = false };
        var body = new TuiTextWidget("body");

        Layout(
            new TuiDock().Add(header, TuiDockSide.Top).Add(body, TuiDockSide.Fill),
            20,
            6);

        Assert.Equal(new TuiRect(0, 0, 20, 6), body.Bounds);
    }

    /// <summary>An edge cannot take more than is left.</summary>
    /// <remarks>
    /// Three headers in two rows is a screen that has run out, and the third has to get
    /// nothing rather than a negative rectangle.
    /// </remarks>
    [Fact]
    public void An_edge_cannot_take_more_than_is_left()
    {
        var third = new TuiTextWidget("third");

        Layout(
            new TuiDock()
                .Add(new TuiTextWidget("one"), TuiDockSide.Top)
                .Add(new TuiTextWidget("two"), TuiDockSide.Top)
                .Add(third, TuiDockSide.Top),
            20,
            2);

        Assert.Equal(0, third.Bounds.Height);
    }

    [Fact]
    public void Every_docked_child_stays_in_the_tree()
    {
        var dock = new TuiDock()
            .Add(new TuiTextWidget("a"), TuiDockSide.Top)
            .Add(new TuiTextWidget("b"), TuiDockSide.Fill);

        Assert.Equal(2, dock.Children.Count);
    }
}
