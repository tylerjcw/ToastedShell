using Tosh.Tui;
using Tosh.Tui.Rendering;

namespace Tosh.Tests;

/// <summary>
/// The cell grid that replaces string frames.
/// </summary>
public sealed class TuiBufferTests
{
    private static TuiBuffer Buffer(int width = 10, int height = 3) => new(new TuiSize(width, height));

    [Fact]
    public void A_new_buffer_is_blank()
    {
        Assert.Equal("          ", Buffer().RowText(0));
    }

    [Fact]
    public void Text_is_drawn_where_it_is_put()
    {
        var buffer = Buffer();
        var used = buffer.DrawText(2, 1, "abc", TuiStyle.Default);

        Assert.Equal(3, used);
        Assert.Equal("  abc     ", buffer.RowText(1));
        Assert.Equal("          ", buffer.RowText(0));
    }

    [Fact]
    public void A_wide_character_occupies_two_cells()
    {
        var buffer = Buffer();
        var used = buffer.DrawText(0, 0, "日", TuiStyle.Default);

        Assert.Equal(2, used);
        Assert.Equal("日", buffer[0, 0].Text);
        Assert.True(buffer[1, 0].IsContinuation);
    }

    [Fact]
    public void A_wide_character_that_would_straddle_the_edge_is_not_drawn()
    {
        var buffer = Buffer(width: 3);

        // Two columns fit, the third would be half a character.
        var used = buffer.DrawText(0, 0, "日本", TuiStyle.Default);

        Assert.Equal(2, used);
        Assert.Equal("日 ", buffer.RowText(0));
    }

    [Fact]
    public void Overwriting_the_left_half_of_a_wide_character_clears_its_other_half()
    {
        var buffer = Buffer();
        buffer.DrawText(0, 0, "日本", TuiStyle.Default);

        buffer.DrawText(0, 0, "x", TuiStyle.Default);

        // The stray right half would otherwise still be on screen with nothing beside it.
        Assert.Equal("x", buffer[0, 0].Text);
        Assert.Equal(" ", buffer[1, 0].Text);
    }

    [Fact]
    public void Overwriting_the_right_half_of_a_wide_character_clears_its_left_half()
    {
        var buffer = Buffer();
        buffer.DrawText(0, 0, "日", TuiStyle.Default);

        buffer.DrawText(1, 0, "x", TuiStyle.Default);

        Assert.Equal(" ", buffer[0, 0].Text);
        Assert.Equal("x", buffer[1, 0].Text);
    }

    [Fact]
    public void A_combining_mark_joins_the_cell_before_it()
    {
        var buffer = Buffer();
        buffer.DrawText(0, 0, "éx", TuiStyle.Default);

        // One column for the accented letter, not two.
        Assert.Equal("é", buffer[0, 0].Text);
        Assert.Equal("x", buffer[1, 0].Text);
    }

    [Fact]
    public void Drawing_respects_a_column_budget()
    {
        var buffer = Buffer();
        var used = buffer.DrawText(0, 0, "abcdef", TuiStyle.Default, maxColumns: 3);

        Assert.Equal(3, used);
        Assert.Equal("abc       ", buffer.RowText(0));
    }

    [Fact]
    public void Drawing_past_the_edge_is_dropped_rather_than_throwing()
    {
        var buffer = Buffer(width: 4);

        Assert.Equal(0, buffer.DrawText(9, 0, "abc", TuiStyle.Default));
        Assert.Equal(0, buffer.DrawText(0, 9, "abc", TuiStyle.Default));
        Assert.Equal(2, buffer.DrawText(2, 0, "abcdef", TuiStyle.Default));
    }

    [Fact]
    public void Filling_a_region_leaves_the_rest_alone()
    {
        var buffer = Buffer();
        buffer.DrawText(0, 0, "abcdefghij", TuiStyle.Default);
        buffer.Fill(new TuiRect(2, 0, 3, 1), new TuiStyle(Background: "blue"));

        Assert.Equal("ab   fghij", buffer.RowText(0));
        Assert.Equal("blue", buffer[3, 0].Style.Background);
    }

    [Fact]
    public void One_buffer_composes_over_another()
    {
        var background = Buffer();
        background.DrawText(0, 0, "..........", TuiStyle.Default);
        background.DrawText(0, 1, "..........", TuiStyle.Default);

        var dialog = new TuiBuffer(new TuiSize(4, 1));
        dialog.DrawText(0, 0, "[ok]", TuiStyle.Default);

        background.Compose(dialog, column: 3, row: 1);

        Assert.Equal("..........", background.RowText(0));
        Assert.Equal("...[ok]...", background.RowText(1));
    }

    [Fact]
    public void Style_travels_with_the_cell()
    {
        var buffer = Buffer();
        var style = new TuiStyle("red", "black", TuiTextAttributes.Bold | TuiTextAttributes.Underline);

        buffer.DrawText(0, 0, "hi", style);

        Assert.Equal(style, buffer[0, 0].Style);
        Assert.Equal(TuiStyle.Default, buffer[5, 0].Style);
    }
}
