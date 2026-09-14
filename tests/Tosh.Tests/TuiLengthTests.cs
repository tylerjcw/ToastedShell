using Tosh.Tui;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

/// <summary>
/// How a container divides space: fixed, auto, a share of the leftovers, a share of the
/// whole — and any of them bounded.
/// </summary>
public sealed class TuiLengthTests
{
    private static string[] Render(TuiWidget widget, int width, int height)
    {
        var buffer = new TuiBuffer(new TuiSize(width, height));
        var bounds = new TuiRect(0, 0, width, height);

        widget.Measure(TuiConstraints.From(new TuiSize(width, height)));
        widget.Arrange(bounds);
        widget.Draw(new TuiSurface(buffer, bounds));

        return [.. Enumerable.Range(0, height).Select(row => buffer.RowText(row))];
    }

    private static int[] Widths(params TuiLength[] sizes)
    {
        var stack = new TuiStack(TuiOrientation.Horizontal);
        var children = sizes.Select(size => new TuiTextWidget("xxxx") { Size = size, Wrap = false }).ToArray();

        foreach (var child in children)
        {
            stack.Add(child);
        }

        stack.Measure(TuiConstraints.From(new TuiSize(100, 1)));
        stack.Arrange(new TuiRect(0, 0, 100, 1));

        return [.. children.Select(child => child.Bounds.Width)];
    }

    [Theory]
    [InlineData("auto", TuiLengthKind.Auto, 0)]
    [InlineData("12", TuiLengthKind.Fixed, 12)]
    [InlineData("*", TuiLengthKind.Star, 1)]
    [InlineData("3*", TuiLengthKind.Star, 3)]
    [InlineData("33%", TuiLengthKind.Fraction, 33)]
    [InlineData("1/3", TuiLengthKind.Fraction, 1)]
    [InlineData("nonsense", TuiLengthKind.Auto, 0)]
    public void A_length_is_written_the_way_a_layout_author_writes_one(string text, TuiLengthKind kind, int value)
    {
        var length = TuiLength.Parse(text);

        Assert.Equal(kind, length.Kind);
        Assert.Equal(value, length.Value);
    }

    [Fact]
    public void Bounds_follow_the_kind_as_a_range()
    {
        var bounded = TuiLength.Parse("33% 24..40");

        Assert.Equal(TuiLengthKind.Fraction, bounded.Kind);
        Assert.Equal(24, bounded.Minimum);
        Assert.Equal(40, bounded.Maximum);

        Assert.Equal(40, TuiLength.Parse("* ..40").Maximum);
        Assert.Null(TuiLength.Parse("* ..40").Minimum);

        Assert.Equal(10, TuiLength.Parse("auto 10..").Minimum);
        Assert.Null(TuiLength.Parse("auto 10..").Maximum);
    }

    [Fact]
    public void A_third_is_not_thirty_three_percent()
    {
        // 118 columns by 33/100 is 38 and by 1/3 is 39, and the author who wrote
        // `width / 3` meant the second.
        Assert.Equal(38, Share(TuiLength.Percent(33), 118));
        Assert.Equal(39, Share(TuiLength.Ratio(1, 3), 118));

        static int Share(TuiLength size, int total)
        {
            var stack = new TuiStack(TuiOrientation.Horizontal);
            var first = new TuiTextWidget("x") { Size = size };

            stack.Add(first).Add(new TuiTextWidget("y"), TuiLength.Star());
            stack.Arrange(new TuiRect(0, 0, total, 1));

            return first.Bounds.Width;
        }
    }

    [Fact]
    public void A_length_reads_back_as_it_was_written()
    {
        Assert.Equal("1/3", TuiLength.Ratio(1, 3).ToString());
        Assert.Equal("33% 24..40", TuiLength.Percent(33).Between(24, 40).ToString());
        Assert.Equal("2*", TuiLength.Star(2).ToString());
        Assert.Equal("auto", TuiLength.Auto.ToString());
        Assert.Equal("12", TuiLength.Fixed(12).ToString());
    }

    [Fact]
    public void A_percent_is_a_share_of_the_whole_and_a_star_is_a_share_of_the_rest()
    {
        // 30 of 100 to the percent child; the remaining 70 split 1:1 between the stars.
        Assert.Equal([30, 35, 35], Widths(TuiLength.Percent(30), TuiLength.Star(), TuiLength.Star()));
    }

    [Fact]
    public void Two_percent_children_each_take_their_share_of_everything()
    {
        Assert.Equal([30, 30, 40], Widths(TuiLength.Percent(30), TuiLength.Percent(30), TuiLength.Star()));
    }

    [Fact]
    public void A_bound_holds_a_percent_where_arithmetic_at_the_call_site_used_to()
    {
        // The help browser's "a third, but never less than 24 nor more than 40" — which it
        // wrote as Math.Clamp and then pinned as a fixed size, so it stopped being a third
        // of anything on the next resize.
        var third = TuiLength.Parse("33% 24..40");

        Assert.Equal(33, Widths(third, TuiLength.Star())[0]);
        Assert.Equal(24, SidebarWidth(third, 30));
        Assert.Equal(40, SidebarWidth(third, 300));

        static int SidebarWidth(TuiLength size, int total)
        {
            var stack = new TuiStack(TuiOrientation.Horizontal);
            var sidebar = new TuiTextWidget("x") { Size = size };

            stack.Add(sidebar).Add(new TuiTextWidget("y"), TuiLength.Star());
            stack.Arrange(new TuiRect(0, 0, total, 1));

            return sidebar.Bounds.Width;
        }
    }

    [Fact]
    public void A_bounded_star_does_not_absorb_the_rounding_error()
    {
        // 100 split three ways is 33, 33, 33 with one left over. The leftover belongs to a
        // child that can take it, not to one pinned at its maximum.
        var widths = Widths(TuiLength.Star().AtMost(33), TuiLength.Star(), TuiLength.Star());

        Assert.Equal(33, widths[0]);
        Assert.Equal(100, widths.Sum());
    }

    [Fact]
    public void A_bound_applies_to_a_fixed_length_too()
    {
        Assert.Equal([20, 80], Widths(TuiLength.Fixed(50).AtMost(20), TuiLength.Star()));
        Assert.Equal([30, 70], Widths(TuiLength.Fixed(5).AtLeast(30), TuiLength.Star()));
    }

    [Fact]
    public void A_row_of_bounded_panes_still_fills_the_row()
    {
        var rows = Render(
            new TuiStack(TuiOrientation.Horizontal)
                .Add(new TuiTextWidget("aaaaaaaaaa") { Size = "25% 4..", Wrap = false })
                .Add(new TuiTextWidget("bbbbbbbbbb") { Size = "*", Wrap = false }),
            12,
            1);

        // Both panes are cut, and both say so: four columns of a ten-column word is
        // "aaa…", not "aaaa". The row is still exactly filled, which is what this is for.
        Assert.Equal("aaa\u2026bbbbbbb\u2026", rows[0]);
    }

    [Fact]
    public void A_long_child_shrinks_rather_than_starving_the_ones_after_it()
    {
        // A status bar is the case: the path on the left grew past the terminal, took the
        // whole row, and the position indicator on the right was arranged at zero width
        // and simply vanished — with no mark to say anything had been cut.
        var position = new TuiTextWidget("Ln 12, Col 4") { Size = "auto" };

        var row = new TuiStack(TuiOrientation.Horizontal)
            .Add(new TuiTextWidget("/a/very/long/path/that/will/not/fit.cs") { Size = "auto" })
            .Add(position);

        var rows = Render(row, 24, 1);

        Assert.True(position.Bounds.Width > 0, "The right-hand segment was starved.");
        Assert.Contains("\u2026", rows[0]);
        Assert.Equal(24, rows[0].Length);
    }

    [Fact]
    public void Everyone_gives_up_the_same_proportion_of_what_they_asked_for()
    {
        var left = new TuiTextWidget("aaaaaaaaaaaaaaaaaaaa") { Size = "auto" };
        var right = new TuiTextWidget("bbbbbbbbbb") { Size = "auto" };

        var row = new TuiStack(TuiOrientation.Horizontal).Add(left).Add(right);

        Render(row, 15, 1);

        // Nobody can ask for more than the row, so the twenty-column word asks for fifteen
        // and the ten-column one for ten. Twenty-five wanted against fifteen to give is
        // three fifths each: nine and six, and the row exactly full.
        Assert.Equal(9, left.Bounds.Width);
        Assert.Equal(6, right.Bounds.Width);
    }

    [Fact]
    public void A_declared_minimum_survives_a_narrow_row()
    {
        // Which is how an author says "this one matters": the position indicator keeps its
        // twelve columns and the path gives up the rest.
        var path = new TuiTextWidget("/a/very/long/path/that/will/not/fit.cs") { Size = "auto" };
        var position = new TuiTextWidget("Ln 12, Col 4") { Size = "auto 12.." };

        Render(new TuiStack(TuiOrientation.Horizontal).Add(path).Add(position), 24, 1);

        Assert.Equal(12, position.Bounds.Width);
        Assert.Equal(12, path.Bounds.Width);
    }

    [Fact]
    public void One_row_of_text_is_a_label_it_is_cut_rather_than_folded()
    {
        // Wrapping onto a second row that will never be drawn shows the first N characters
        // and stops, which reads as a shorter path rather than as a cut one.
        var label = new TuiTextWidget("/home/komrad/projects/tosh/notes.md") { Wrap = true };

        Assert.Equal(["/home/komrad/\u2026"], Render(label, 14, 1));
    }

    [Fact]
    public void More_than_one_row_still_wraps()
    {
        var block = new TuiTextWidget("one two three four") { Wrap = true };

        Assert.Equal(["one two", "three", "four"], Render(block, 8, 3).Select(row => row.TrimEnd()));
    }
}
