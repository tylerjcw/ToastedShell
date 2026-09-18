using Tosh.Tui;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

/// <summary>
/// Rows and columns that line up across the whole grid (<c>TUI-0002</c>).
/// </summary>
/// <remarks>
/// A row of columns is a stack of stacks until two rows need the same column widths. Nested
/// stacks cannot do that — each divides its own line and knows nothing about its neighbours —
/// so the author ends up computing widths by hand, which is what the layout system exists to
/// stop.
/// </remarks>
public sealed class TuiGridTests
{
    private static IReadOnlyList<string> Render(TuiWidget widget, int width, int height)
    {
        var buffer = new TuiBuffer(new TuiSize(width, height));

        widget.Measure(new TuiConstraints(width, height));
        widget.Arrange(new TuiRect(0, 0, width, height));
        widget.Draw(new TuiSurface(buffer, new TuiRect(0, 0, width, height)));

        return [.. Enumerable.Range(0, height).Select(row => buffer.RowText(row).TrimEnd())];
    }

    private static TuiGrid Fields(params (string Label, string Value)[] fields)
    {
        var grid = new TuiGrid("auto, *");

        for (var row = 0; row < fields.Length; row += 1)
        {
            grid.Add(new TuiTextWidget(fields[row].Label), row, 0);
            grid.Add(new TuiTextWidget(fields[row].Value), row, 1);
        }

        return grid;
    }

    /// <summary>
    /// A column is as wide as the widest thing in it, in every row.
    /// </summary>
    /// <remarks>
    /// The property the whole container exists for: the values line up under each other
    /// because the label column agreed on one width, which nested stacks cannot arrange.
    /// </remarks>
    [Fact]
    public void A_column_is_the_same_width_in_every_row()
    {
        var grid = Fields(("Name", "tosh"), ("Assemblies", "12"), ("Cache", "11 MB"));
        var rows = Render(grid, 40, 3);

        // "Assemblies" is the longest label, so every value starts one column past it.
        Assert.Equal("Name       tosh", rows[0]);
        Assert.Equal("Assemblies 12", rows[1]);
        Assert.Equal("Cache      11 MB", rows[2]);

        // Asserted on where the children were put rather than on what they drew: a widget
        // elides text that does not fit its slot, so reading the glyphs back measures the
        // text widget as much as the grid.
        var values = grid.Children.Where((_, index) => index % 2 == 1).ToArray();

        Assert.All(values, value => Assert.Equal("Assemblies".Length + 1, value.Bounds.Left));
    }

    [Fact]
    public void A_child_can_span_columns()
    {
        var grid = new TuiGrid("*, *, *", "auto, auto");

        grid.Add(new TuiTextWidget("a header across all three"), 0, 0, 1, 3);
        grid.Add(new TuiTextWidget("one"), 1, 0);
        grid.Add(new TuiTextWidget("two"), 1, 1);
        grid.Add(new TuiTextWidget("three"), 1, 2);

        var rows = Render(grid, 40, 2);

        Assert.Equal("a header across all three", rows[0]);
        Assert.Contains("one", rows[1], StringComparison.Ordinal);
        Assert.Contains("three", rows[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// A column with nothing visible in it takes no cells.
    /// </summary>
    /// <remarks>
    /// A hidden child is not a child with nothing in it. Leaving its column at full width
    /// would put a stripe of blank cells down the middle of the grid.
    /// </remarks>
    [Fact]
    public void A_column_with_nothing_visible_closes_up()
    {
        var grid = new TuiGrid("auto, auto, auto");
        var middle = new TuiTextWidget("MIDDLE") { IsVisible = false };

        grid.Add(new TuiTextWidget("left"), 0, 0);
        grid.Add(middle, 0, 1);
        grid.Add(new TuiTextWidget("right"), 0, 2);

        Assert.Equal("left right", Render(grid, 30, 1)[0]);
    }

    /// <summary>Columns nobody declared are stars; rows nobody declared are auto.</summary>
    /// <remarks>
    /// Each is what its axis is usually for: a column divides the width it was given, a row
    /// is as tall as what is in it. Rows that each took an equal share of the height would
    /// put blank lines under every label.
    /// </remarks>
    [Fact]
    public void Undeclared_tracks_take_the_default_for_their_axis()
    {
        var grid = new TuiGrid();

        grid.Add(new TuiTextWidget("a"), 0, 0);
        grid.Add(new TuiTextWidget("b"), 0, 1);
        grid.Add(new TuiTextWidget("c"), 1, 0);

        Assert.Equal(2, grid.ColumnCount);
        Assert.Equal(2, grid.RowCount);

        var rows = Render(grid, 20, 4);

        // Two star columns split twenty cells minus the gap, so the second starts near ten.
        Assert.StartsWith("a", rows[0], StringComparison.Ordinal);
        Assert.Contains("b", rows[0], StringComparison.Ordinal);

        // Auto rows are one line each, so the third row of the surface is blank.
        Assert.Equal("c", rows[1]);
        Assert.Equal(string.Empty, rows[2]);
    }

    /// <summary>
    /// The track sizes are the stack's arithmetic, so every length spelling works.
    /// </summary>
    /// <remarks>
    /// Shared through <see cref="TuiTracks"/> rather than written twice. A grid that sized
    /// its tracks by its own rules would disagree with a stack about what <c>25%</c> means,
    /// which is the sort of difference nobody finds until a pane is one column out.
    /// </remarks>
    [Fact]
    public void A_fixed_column_takes_exactly_what_it_asked_for()
    {
        var grid = new TuiGrid("8, *");
        var wide = new TuiTextWidget("12345678901234");
        var rest = new TuiTextWidget("rest");

        grid.Add(wide, 0, 0);
        grid.Add(rest, 0, 1);

        Render(grid, 30, 1);

        // Eight cells whatever the child wanted, then the gap, then the rest.
        Assert.Equal(8, wide.Bounds.Width);
        Assert.Equal(9, rest.Bounds.Left);
        Assert.Equal(21, rest.Bounds.Width);
    }

    /// <summary>
    /// A declared minimum is honoured even when there is not room for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "this column is at least six wide" has to survive a terminal too narrow for
    /// everything, which is exactly when it matters.
    /// </para>
    /// <para>
    /// What it does <em>not</em> do is take the room from its neighbours. Nine cells cannot
    /// hold a five-wide label, a gap and a six-wide column, and the label keeps its five —
    /// so the row runs past the grid and the surface clips the end of it. That is the shared
    /// allocator's behaviour, inherited from the stack: a bounded star is clamped after the
    /// share is worked out, and only an <em>unbounded</em> star absorbs what is left over.
    /// Recorded here rather than asserted around, because a minimum that silently stopped
    /// being a minimum would be worse.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_minimum_on_a_column_is_honoured_even_when_it_does_not_fit()
    {
        var grid = new TuiGrid("auto, * 6..");
        var label = new TuiTextWidget("label");
        var value = new TuiTextWidget("abcdef");

        grid.Add(label, 0, 0);
        grid.Add(value, 0, 1);

        Render(grid, 9, 1);

        Assert.Equal(6, value.Bounds.Width);

        // And where there is room, nothing is taken from anybody.
        Render(grid, 20, 1);

        Assert.Equal(5, label.Bounds.Width);
        Assert.Equal(14, value.Bounds.Width);
    }

    [Fact]
    public void The_gap_between_columns_can_be_closed()
    {
        var grid = new TuiGrid("auto, auto") { ColumnGap = 0 };

        grid.Add(new TuiTextWidget("ab"), 0, 0);
        grid.Add(new TuiTextWidget("cd"), 0, 1);

        Assert.Equal("abcd", Render(grid, 20, 1)[0]);
    }

    [Fact]
    public void An_empty_grid_draws_nothing_and_counts_nothing()
    {
        var grid = new TuiGrid();

        Assert.Equal(0, grid.ColumnCount);
        Assert.Equal(0, grid.RowCount);
        Assert.All(Render(grid, 10, 2), row => Assert.Equal(string.Empty, row));
    }

    [Fact]
    public void Every_child_stays_in_the_tree()
    {
        var grid = Fields(("a", "1"), ("b", "2"));

        Assert.Equal(4, grid.Children.Count);
    }
}
