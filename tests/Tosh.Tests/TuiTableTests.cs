using System.Dynamic;
using Tosh.Tui;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

/// <summary>
/// Rows of objects in columns — the control an object shell most obviously needed.
/// </summary>
public sealed class TuiTableTests
{
    private static string[] Render(TuiWidget widget, int width, int height)
    {
        var buffer = new TuiBuffer(new TuiSize(width, height));
        var bounds = new TuiRect(0, 0, width, height);

        widget.Measure(TuiConstraints.From(new TuiSize(width, height)));
        widget.Arrange(bounds);
        widget.Draw(new TuiSurface(buffer, bounds));

        return [.. Enumerable.Range(0, height).Select(row => buffer.RowText(row).TrimEnd())];
    }

    private static TuiTable Focused(TuiTable table)
    {
        new TuiFocus(table).Focus(table);
        return table;
    }

    private static TuiInputEvent Key(ConsoleKey key)
        => TuiInputEvent.FromKey(new ConsoleKeyInfo('\0', key, false, false, false));

    private static object Record(params (string Key, object? Value)[] fields)
    {
        var record = new ExpandoObject();
        var dictionary = (IDictionary<string, object?>)record;

        foreach (var (key, value) in fields)
        {
            dictionary[key] = value;
        }

        return record;
    }

    private static TuiTable Processes() => new([
        Record(("Name", "bash"), ("Memory", 12)),
        Record(("Name", "tosh"), ("Memory", 340)),
        Record(("Name", "code"), ("Memory", 1024)),
    ]);

    [Fact]
    public void Columns_come_from_the_rows_when_nobody_says_what_they_are()
    {
        // The whole point of `ls | tui table`: a pipeline's objects already know what they
        // are made of, so asking the caller to list their fields is ritual.
        var rows = Render(Processes(), 20, 4);

        Assert.Equal("Name Memory", rows[0]);
        Assert.Equal("bash 12", rows[1]);
        Assert.Equal("tosh 340", rows[2]);
        Assert.Equal("code 1024", rows[3]);
    }

    [Fact]
    public void A_column_is_as_wide_as_the_widest_thing_in_it_including_its_header()
    {
        var table = new TuiTable([Record(("Id", 1)), Record(("Id", 100000))]);

        // "100000" is six wide and "Id" is two, so the column is six.
        Assert.Equal(["Id", "1", "100000"], Render(table, 20, 3));
    }

    [Fact]
    public void A_column_can_be_named_aligned_and_sized()
    {
        var table = Processes();

        table.Columns =
        [
            new TuiColumn("Process", "Name"),
            new TuiColumn("MB", "Memory") { Align = TuiAlignment.Right, Width = TuiLength.Fixed(6) },
        ];

        var rows = Render(table, 20, 4);

        Assert.Equal("Process     MB", rows[0]);
        Assert.Equal("bash        12", rows[1]);
        Assert.Equal("code      1024", rows[3]);
    }

    [Fact]
    public void A_star_column_takes_what_is_left()
    {
        var table = Processes();

        table.Columns =
        [
            new TuiColumn("Name") { Width = TuiLength.Fixed(6) },
            new TuiColumn("Memory") { Width = TuiLength.Star(), Align = TuiAlignment.Right },
        ];

        var rows = Render(table, 20, 2);

        // 6 for the name, a gap, and the remaining 13 for the number, right-aligned.
        Assert.Equal(20, rows[1].PadRight(20).Length);
        Assert.EndsWith("12", rows[1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_cell_wider_than_its_column_is_truncated_rather_than_spilling()
    {
        var table = new TuiTable([Record(("Name", "an extremely long value"))]);
        table.Columns = [new TuiColumn("Name") { Width = TuiLength.Fixed(8) }];

        Assert.Equal(["Name", "an extr…"], Render(table, 20, 2));
    }

    [Fact]
    public void The_header_stays_put_while_the_rows_move()
    {
        var table = Focused(new TuiTable(
            Enumerable.Range(1, 40).Select(n => Record(("N", n)))));

        table.Measure(TuiConstraints.From(new TuiSize(10, 4)));
        table.Arrange(new TuiRect(0, 0, 10, 4));

        for (var press = 0; press < 10; press += 1)
        {
            table.OnInput(Key(ConsoleKey.DownArrow));
        }

        var rows = Render(table, 10, 4);

        Assert.Equal("N", rows[0]);
        Assert.Equal(10, table.SelectedIndex);
        Assert.Contains("11", rows[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_can_be_selected_and_activated()
    {
        object? activated = null;
        var table = Focused(Processes());
        table.Activated = row => activated = row;

        table.OnInput(Key(ConsoleKey.DownArrow));
        table.OnInput(Key(ConsoleKey.Enter));

        Assert.Equal(1, table.SelectedIndex);
        Assert.Same(table.Rows[1], activated);
        Assert.Same(table.Rows[1], table.Value);
    }

    [Fact]
    public void Enter_is_left_alone_when_nothing_is_listening()
    {
        // The rule TuiList and TuiTextField already follow: with no handler, activation
        // belongs to whatever surrounds the widget — usually a form waiting to be submitted.
        Assert.False(Focused(Processes()).OnInput(Key(ConsoleKey.Enter)));
    }

    [Fact]
    public void Clicking_a_row_selects_it_and_the_header_is_not_a_row()
    {
        var table = Processes();
        table.Arrange(new TuiRect(0, 0, 20, 4));

        var click = (int row) => TuiInputEvent.FromMouse(
            new TuiMouseEvent(TuiMouseAction.Press, TuiMouseButton.Left, 2, row, false, false, false));

        table.OnInput(click(3));
        Assert.Equal(2, table.SelectedIndex);

        table.OnInput(click(0));
        Assert.Equal(2, table.SelectedIndex);
    }

    [Fact]
    public void Rows_replaced_underneath_a_reader_keep_the_selection()
    {
        var table = Focused(Processes());
        table.Arrange(new TuiRect(0, 0, 20, 4));
        table.OnInput(Key(ConsoleKey.End));

        table.Rows = [Record(("Name", "x")), Record(("Name", "y")), Record(("Name", "z"))];
        Assert.Equal(2, table.SelectedIndex);

        table.Rows = [Record(("Name", "only"))];
        Assert.Equal(0, table.SelectedIndex);
    }

    [Fact]
    public void A_clr_object_works_as_well_as_a_record()
    {
        var table = new TuiTable([new Version(1, 2), new Version(3, 4)]);
        table.Columns = [new TuiColumn("Major"), new TuiColumn("Minor")];

        Assert.Equal(["Major Minor", "1     2", "3     4"], Render(table, 20, 3));
    }

    [Fact]
    public void A_column_can_style_itself_by_what_the_cell_holds()
    {
        var table = new TuiTable([Record(("State", "ok")), Record(("State", "failed"))]);

        table.Columns =
        [
            new TuiColumn("State")
            {
                StyleSelector = value => new TuiStyle(value?.ToString() == "failed" ? "red" : "green"),
            },
        ];

        var buffer = new TuiBuffer(new TuiSize(20, 3));
        table.Arrange(new TuiRect(0, 0, 20, 3));
        table.Draw(new TuiSurface(buffer, new TuiRect(0, 0, 20, 3)));

        // Row 0 is selected, and a selected row is drawn as selected whatever its column
        // would have said — the reader has to be able to see where they are.
        Assert.Equal(TuiTextAttributes.Reverse, buffer[0, 1].Style.Attributes);
        Assert.Equal("red", buffer[0, 2].Style.Foreground);

        table.SelectedIndex = 1;
        table.Draw(new TuiSurface(buffer, new TuiRect(0, 0, 20, 3)));

        Assert.Equal("green", buffer[0, 1].Style.Foreground);
    }

    [Fact]
    public void By_default_a_table_draws_no_grid()
    {
        // A table inside a TuiBorder already has an outline, and a second one around it is
        // a double rule.
        Assert.Equal(TuiTableBorders.None, new TuiTable().Borders);
    }

    [Fact]
    public void A_header_rule_stops_the_headings_reading_as_a_first_row()
    {
        var table = Processes();
        table.Borders = TuiTableBorders.Header;

        // The rule spans the pane, as a rule does; the junction sits where the columns meet.
        var rows = Render(table, 11, 5);

        Assert.Equal("Name Memory", rows[0]);
        Assert.Equal("────┼──────", rows[1]);
        Assert.Equal("bash 12", rows[2]);
    }

    [Fact]
    public void A_full_grid_is_what_the_shell_prints_at_the_prompt()
    {
        var table = Processes();
        table.Borders = TuiTableBorders.All;

        // Measured, so the grid is exactly as wide as the table wants to be. Given more
        // room it fills it, as every widget does.
        var natural = table.Measure(TuiConstraints.Unbounded);
        var rows = Render(table, natural.Width, natural.Height);

        Assert.Equal(17, natural.Width);
        Assert.Equal("╭──────┬────────╮", rows[0]);
        Assert.Equal("│ Name │ Memory │", rows[1]);
        Assert.Equal("├──────┼────────┤", rows[2]);
        Assert.Equal("│ bash │ 12     │", rows[3]);
        Assert.Equal("│ tosh │ 340    │", rows[4]);
        Assert.Equal("│ code │ 1024   │", rows[5]);
        Assert.Equal("╰──────┴────────╯", rows[6]);
    }

    [Fact]
    public void A_grid_follows_the_glyphs_it_is_given()
    {
        var table = Processes();
        table.Borders = TuiTableBorders.All;
        table.Glyphs = TuiBorderGlyphs.Ascii;

        Assert.Equal("+------+--------+", Render(table, 17, 7)[0]);
    }

    [Fact]
    public void A_click_lands_on_the_row_it_looks_like_through_a_grid()
    {
        var table = Processes();
        table.Borders = TuiTableBorders.All;
        table.Arrange(new TuiRect(0, 0, 20, 7));

        table.OnInput(TuiInputEvent.FromMouse(
            new TuiMouseEvent(TuiMouseAction.Press, TuiMouseButton.Left, 3, 4, false, false, false)));

        Assert.Equal(1, table.SelectedIndex);
    }

    [Fact]
    public void More_columns_than_fit_can_be_scrolled_along()
    {
        // Truncated with no way to move along them, the rightmost columns are data the
        // reader simply cannot see.
        var table = Focused(new TuiTable([
            Record(("A", "aaa"), ("B", "bbb"), ("C", "ccc"), ("D", "ddd")),
        ]));

        table.Arrange(new TuiRect(0, 0, 9, 2));

        Assert.False(table.AllColumnsFit);

        // What matters is which column is leftmost: scrolling drops whole columns off the
        // left, and whatever then fits on the right fills the room.
        Assert.StartsWith("A", Render(table, 9, 2)[0], StringComparison.Ordinal);
        Assert.StartsWith("aaa", Render(table, 9, 2)[1], StringComparison.Ordinal);

        table.OnInput(Key(ConsoleKey.RightArrow));
        Assert.Equal(1, table.ColumnOffset);
        Assert.StartsWith("B", Render(table, 9, 2)[0], StringComparison.Ordinal);
        Assert.StartsWith("bbb", Render(table, 9, 2)[1], StringComparison.Ordinal);

        table.OnInput(Key(ConsoleKey.RightArrow));
        table.OnInput(Key(ConsoleKey.RightArrow));
        Assert.Equal(["D", "ddd"], Render(table, 9, 2));

        // The last column stays put rather than scrolling off into nothing.
        table.OnInput(Key(ConsoleKey.RightArrow));
        Assert.Equal(3, table.ColumnOffset);

        table.OnInput(Key(ConsoleKey.LeftArrow));
        Assert.Equal(2, table.ColumnOffset);
    }

    [Fact]
    public void A_table_that_fits_says_so_and_does_not_move()
    {
        var table = Focused(Processes());
        table.Arrange(new TuiRect(0, 0, 40, 4));

        Assert.True(table.AllColumnsFit);

        table.OnInput(Key(ConsoleKey.LeftArrow));
        Assert.Equal(0, table.ColumnOffset);
    }

    [Fact]
    public void A_table_with_no_rows_draws_nothing_and_answers_nothing()
    {
        var table = new TuiTable();

        Assert.Null(table.SelectedRow);
        Assert.Equal(["", ""], Render(table, 10, 2));
    }
}
