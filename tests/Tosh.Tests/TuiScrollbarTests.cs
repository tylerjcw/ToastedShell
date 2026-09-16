using Tosh.Tui;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

/// <summary>
/// Reserving the column and drawing the bar, which are one decision (<c>TUI-0007</c>).
/// </summary>
public sealed class TuiScrollbarTests
{
    private static (TuiBuffer Buffer, TuiSurface Surface) Sheet(int width, int height)
    {
        var buffer = new TuiBuffer(new TuiSize(width, height));

        return (buffer, new TuiSurface(buffer, new TuiRect(0, 0, width, height)));
    }

    private static string Column(TuiBuffer buffer, int column)
        => string.Concat(Enumerable.Range(0, buffer.Height).Select(row => buffer[column, row].Text));

    private static string[] Rows(TuiWidget widget, int width, int height)
    {
        var buffer = new TuiBuffer(new TuiSize(width, height));
        var bounds = new TuiRect(0, 0, width, height);

        widget.Measure(TuiConstraints.From(new TuiSize(width, height)));
        widget.Arrange(bounds);
        widget.Paint(new TuiSurface(buffer, bounds));

        return [.. Enumerable.Range(0, height).Select(row => buffer.RowText(row))];
    }

    [Fact]
    public void Content_that_fits_keeps_the_whole_width()
    {
        var (_, surface) = Sheet(10, 4);

        Assert.Equal(10, TuiScrollbar.Fit(surface, wanted: true, offset: 0, contentLength: 4).Width);
    }

    [Fact]
    public void Content_that_does_not_fit_gives_up_a_column()
    {
        var (buffer, surface) = Sheet(10, 4);

        Assert.Equal(9, TuiScrollbar.Fit(surface, wanted: true, offset: 0, contentLength: 40).Width);
        Assert.Equal("█│││", Column(buffer, 9));
    }

    [Fact]
    public void A_widget_that_does_not_want_one_keeps_its_width_and_gets_no_bar()
    {
        var (buffer, surface) = Sheet(10, 4);

        Assert.Equal(10, TuiScrollbar.Fit(surface, wanted: false, offset: 0, contentLength: 40).Width);
        Assert.Equal("    ", Column(buffer, 9));
    }

    [Fact]
    public void A_bar_can_start_below_a_header()
    {
        // The one thing that differs between widgets: a table's bar runs beside its rows
        // and not beside its header.
        var (buffer, surface) = Sheet(10, 4);

        TuiScrollbar.Fit(surface, wanted: true, offset: 0, contentLength: 40, firstRow: 1);

        Assert.Equal(" █││", Column(buffer, 9));
    }

    [Fact]
    public void How_many_rows_show_can_differ_from_the_height()
    {
        // A header takes one of them, so four rows of content in a four-row surface with a
        // header does not fit and does want a bar.
        var (buffer, surface) = Sheet(10, 4);

        TuiScrollbar.Fit(surface, wanted: true, offset: 0, contentLength: 4, firstRow: 1, visibleRows: 3);

        Assert.NotEqual("    ", Column(buffer, 9));
    }

    [Theory]
    [InlineData("list")]
    [InlineData("lines")]
    [InlineData("tree")]
    public void Every_scrolling_widget_reserves_the_column_it_draws_in(string which)
    {
        // The duplication this removed was two conditions per widget that had to agree:
        // one to reserve the column and one to draw. Content must never reach the bar.
        var items = Enumerable.Range(0, 30).Select(index => $"item{index}").ToArray();

        TuiWidget widget = which switch
        {
            "list" => new TuiList(items) { Scrollbar = true },
            "lines" => new TuiLines(items.Select(text => new TuiSpanLine([new TuiSpan(text)]))) { Scrollbar = true },
            _ => Expanded(items),
        };

        var rows = Rows(widget, 14, 5);

        Assert.All(rows, row => Assert.Contains(row[^1], "█│"));
    }

    /// <summary>A tree with more rows than fit, which means one that is open.</summary>
    private static TuiTree Expanded(string[] items)
    {
        var tree = new TuiTree("root")
        {
            Scrollbar = true,
            ChildrenOf = node => $"{node}" == "root" ? items : [],
        };

        tree.Expand("root");
        return tree;
    }

    [Fact]
    public void A_table_draws_its_bar_beside_the_rows_and_not_the_header()
    {
        var table = new TuiTable(Enumerable.Range(0, 30).Select(index => new { Name = $"row{index}" }))
        {
            Scrollbar = true,
            ShowHeader = true,
        };

        var rows = Rows(table, 16, 5);

        Assert.DoesNotContain(rows[0][^1], "█│");
        Assert.All(rows.Skip(1), row => Assert.Contains(row[^1], "█│"));
    }
}
