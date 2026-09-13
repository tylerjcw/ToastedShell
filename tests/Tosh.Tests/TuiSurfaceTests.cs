using Tosh.Tui;
using Tosh.Tui.Rendering;

namespace Tosh.Tests;

/// <summary>
/// Surfaces: a widget draws from its own corner and cannot escape its region.
/// </summary>
public sealed class TuiSurfaceTests
{
    [Fact]
    public void A_surface_addresses_from_its_own_corner()
    {
        var buffer = new TuiBuffer(new TuiSize(10, 3));
        var surface = new TuiSurface(buffer, new TuiRect(3, 1, 5, 1));

        surface.DrawText(0, 0, "abc", TuiStyle.Default);

        // The widget wrote to (0,0); it landed where the container put it.
        Assert.Equal("   abc    ", buffer.RowText(1));
        Assert.Equal("          ", buffer.RowText(0));
    }

    [Fact]
    public void Drawing_wider_than_the_surface_is_clipped_not_spilled()
    {
        var buffer = new TuiBuffer(new TuiSize(10, 1));
        var surface = new TuiSurface(buffer, new TuiRect(2, 0, 3, 1));

        var used = surface.DrawText(0, 0, "abcdefgh", TuiStyle.Default);

        Assert.Equal(3, used);
        Assert.Equal("  abc     ", buffer.RowText(0));
    }

    [Fact]
    public void Drawing_outside_the_surface_is_dropped()
    {
        var buffer = new TuiBuffer(new TuiSize(10, 3));
        var surface = new TuiSurface(buffer, new TuiRect(2, 1, 4, 1));

        surface.DrawText(0, 5, "below", TuiStyle.Default);
        surface.DrawText(-3, 0, "left", TuiStyle.Default);
        surface.DrawText(9, 0, "right", TuiStyle.Default);

        Assert.Equal("          ", buffer.RowText(0));
        Assert.Equal("          ", buffer.RowText(2));
    }

    [Fact]
    public void A_surface_cannot_be_larger_than_its_buffer()
    {
        var buffer = new TuiBuffer(new TuiSize(4, 2));
        var surface = new TuiSurface(buffer, new TuiRect(0, 0, 99, 99));

        Assert.Equal(4, surface.Width);
        Assert.Equal(2, surface.Height);
    }

    [Fact]
    public void Clipping_nests_and_stays_inside_its_parent()
    {
        var buffer = new TuiBuffer(new TuiSize(10, 3));
        var outer = new TuiSurface(buffer, new TuiRect(2, 0, 6, 3));
        var inner = outer.Clip(new TuiRect(1, 1, 3, 1));

        inner.DrawText(0, 0, "xyz", TuiStyle.Default);

        Assert.Equal("   xyz    ", buffer.RowText(1));

        // A child asking for more than its parent has gets its parent's bounds.
        var greedy = outer.Clip(new TuiRect(0, 0, 99, 99));
        Assert.Equal(6, greedy.Width);
    }

    [Fact]
    public void Filling_a_surface_leaves_the_rest_of_the_buffer_alone()
    {
        var buffer = new TuiBuffer(new TuiSize(6, 2));
        buffer.DrawText(0, 0, "aaaaaa", TuiStyle.Default);
        buffer.DrawText(0, 1, "bbbbbb", TuiStyle.Default);

        new TuiSurface(buffer, new TuiRect(2, 0, 2, 1)).Fill(new TuiStyle(Background: "red"));

        Assert.Equal("aa  aa", buffer.RowText(0));
        Assert.Equal("bbbbbb", buffer.RowText(1));
        Assert.Equal("red", buffer[2, 0].Style.Background);
    }

    [Fact]
    public void Reading_outside_a_surface_is_blank_rather_than_a_neighbour()
    {
        var buffer = new TuiBuffer(new TuiSize(10, 1));
        buffer.DrawText(0, 0, "secret", TuiStyle.Default);

        var surface = new TuiSurface(buffer, new TuiRect(6, 0, 4, 1));

        Assert.Equal(" ", surface.Get(0, 0).Text);
        Assert.Equal(" ", surface.Get(-1, 0).Text);
    }
}
