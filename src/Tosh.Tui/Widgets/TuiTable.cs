using System.Reflection;
using Tosh.Runtime;
using Tosh.Tui.Rendering;

namespace Tosh.Tui.Widgets;

/// <summary>How much of a grid a table draws.</summary>
public enum TuiTableBorders
{
    /// <summary>Columns separated by blanks, and nothing else drawn.</summary>
    None,

    /// <summary>A rule under the header, so the headings do not read as a first row.</summary>
    Header,

    /// <summary>An outline, a rule under the header, and a line between each column.</summary>
    All,
}

/// <summary>Where a cell's text sits when it is narrower than its column.</summary>
public enum TuiAlignment
{
    Left,
    Center,
    Right,
}

/// <summary>One column of a <see cref="TuiTable"/>.</summary>
/// <remarks>
/// A column is a name, a way of getting a value out of a row, and a width. The width is a
/// <see cref="TuiLength"/> — the same vocabulary a layout uses — so a table's columns are
/// divided by the same rules as a screen's panes rather than by a second set.
/// </remarks>
public sealed class TuiColumn
{
    public TuiColumn(string header, string? property = null)
    {
        Header = header ?? string.Empty;
        Property = property ?? header;
    }

    /// <summary>What the header row says.</summary>
    public string Header { get; set; }

    /// <summary>The property or field read from each row.</summary>
    public string? Property { get; set; }

    /// <summary>Reads the cell from the row, when a property name will not do.</summary>
    public Func<object?, object?>? Selector { get; set; }

    /// <summary>How wide the column is. Auto measures the content, including the header.</summary>
    public TuiLength Width { get; set; } = TuiLength.Auto;

    /// <summary>Where the text sits in the column.</summary>
    public TuiAlignment Align { get; set; }

    /// <summary>How the cells are drawn.</summary>
    public TuiStyle Style { get; set; }

    /// <summary>Styles a cell by its value, for a column that means something.</summary>
    public Func<object?, TuiStyle>? StyleSelector { get; set; }

    /// <summary>The value this column reads out of a row.</summary>
    public object? ValueOf(object? row)
        => Selector is not null ? Selector(row) : Read(row, Property);

    private static object? Read(object? row, string? name)
    {
        if (row is null || string.IsNullOrEmpty(name))
        {
            return row;
        }

        if (ShellRecordUtilities.TryGetValue(row, name, out var value))
        {
            return value;
        }

        return row.GetType()
            .GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
            ?.GetValue(row);
    }
}

/// <summary>
/// Rows of objects, in columns.
/// </summary>
/// <remarks>
/// <para>
/// The control an object shell most obviously needs, and the one it did not have: `ls`
/// renders columns everywhere except inside the TUI, so a reader saw a table at the prompt
/// and a column of `ToString()` in a picker (<c>TUI-0017</c>).
/// </para>
/// <para>
/// Columns are derived from the first row when none are given, which is what makes
/// <c>ls | tui table</c> work with nothing written down. A caller who wants different
/// columns says so, and the shell's own idea of an object's fields is what both use.
/// </para>
/// <code>
/// var table = new TuiTable($processes)
///
/// $table.Columns = [
///     new TuiColumn("Name"),
///     new TuiColumn("Memory") {| Align = TuiAlignment.Right, Width = "12" |}
/// ]
/// </code>
/// </remarks>
public sealed class TuiTable : TuiWidget
{
    private IReadOnlyList<object?> _rows = [];
    private IReadOnlyList<TuiColumn> _columns = [];
    private int _selectedIndex;
    private int _offset;

    public TuiTable(IEnumerable<object?>? rows = null)
    {
        if (rows is not null)
        {
            Rows = [.. rows];
        }
    }

    /// <summary>The rows.</summary>
    /// <remarks>
    /// Replacing them keeps the selection where it was, clamped, so a table refreshed
    /// underneath a reader does not jump back to the top.
    /// </remarks>
    public IReadOnlyList<object?> Rows
    {
        get => _rows;
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            _rows = value;
            SelectedIndex = _selectedIndex;
        }
    }

    /// <summary>A script function supplying the rows, re-read on every redraw.</summary>
    public IShellCallable? RowsSource { get; set; }

    /// <summary>
    /// The columns, or empty to take them from the first row.
    /// </summary>
    /// <remarks>
    /// Derived rather than required, because a pipeline's objects already know what they
    /// are made of. Asking the caller to list the fields of something they just piped in
    /// is the ritual this widget exists to remove.
    /// </remarks>
    public IReadOnlyList<TuiColumn> Columns
    {
        get => _columns.Count > 0 ? _columns : DeriveColumns();
        set => _columns = value ?? [];
    }

    /// <summary>Whether the header row is drawn.</summary>
    public bool ShowHeader { get; set; } = true;

    /// <summary>
    /// How much of a grid is drawn around and between the cells.
    /// </summary>
    /// <remarks>
    /// <see cref="TuiTableBorders.None"/> by default, because a table inside a
    /// <see cref="TuiBorder"/> already has an outline and a second one around it is a
    /// double rule. <see cref="TuiTableBorders.All"/> is what the shell prints at the
    /// prompt, and what a table standing on its own wants.
    /// </remarks>
    public TuiTableBorders Borders { get; set; }

    /// <summary>Which characters the grid is drawn with.</summary>
    public TuiBorderGlyphs Glyphs { get; set; } = TuiBorderGlyphs.Rounded;

    /// <summary>How the grid is drawn.</summary>
    public TuiStyle BorderStyle { get; set; } = new(Attributes: TuiTextAttributes.Dim);

    /// <summary>How the header row is drawn.</summary>
    public TuiStyle HeaderStyle { get; set; } = new(Attributes: TuiTextAttributes.Bold);

    /// <summary>How the selected row is drawn.</summary>
    public TuiStyle SelectedStyle { get; set; } = new(Attributes: TuiTextAttributes.Reverse);

    /// <summary>Blank columns between one column and the next.</summary>
    /// <remarks>
    /// A grid pads each cell by one on both sides and puts a rule between, so a bordered
    /// table spends three columns where a plain one spends this.
    /// </remarks>
    public int Gap { get; set; } = 1;

    /// <summary>Columns spent between one column and the next, grid included.</summary>
    private int Separator => Borders == TuiTableBorders.All ? 3 : Gap;

    /// <summary>Rows and columns the frame takes before any cell is drawn.</summary>
    private int FrameRows => Borders switch
    {
        TuiTableBorders.All => ShowHeader ? 3 : 2,
        TuiTableBorders.Header => ShowHeader ? 1 : 0,
        _ => 0,
    };

    private int FrameColumns => Borders == TuiTableBorders.All ? 4 : 0;

    /// <summary>Which row is selected.</summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            var clamped = _rows.Count == 0 ? 0 : Math.Clamp(value, 0, _rows.Count - 1);

            if (clamped == _selectedIndex)
            {
                EnsureVisible();
                return;
            }

            _selectedIndex = clamped;
            EnsureVisible();
            SelectionChanged?.Invoke(_selectedIndex);
        }
    }

    /// <summary>The selected row, or null when there are none.</summary>
    public object? SelectedRow => _selectedIndex < _rows.Count ? _rows[_selectedIndex] : null;

    /// <summary>The first row shown.</summary>
    public int Offset => _offset;

    /// <summary>The first column shown.</summary>
    /// <remarks>
    /// A table with more columns than fit truncates the ones on the right, and a reader
    /// with no way to move along them cannot see the data at all. Whole columns rather
    /// than cells: half a column is not a reading of anything.
    /// </remarks>
    public int ColumnOffset
    {
        get => field;
        private set => field = Math.Clamp(value, 0, Math.Max(0, Columns.Count - 1));
    }

    /// <summary>Moves the view along by whole columns.</summary>
    public void ScrollColumns(int by) => ColumnOffset += by;

    /// <summary>Raised when the selection moves.</summary>
    public Action<int>? SelectionChanged { get; set; }

    /// <summary>Raised when a row is chosen with Enter.</summary>
    public Action<object?>? Activated { get; set; }

    /// <inheritdoc />
    public override bool Activate()
    {
        if (Activated is null)
        {
            return false;
        }

        Activated(SelectedRow);
        return true;
    }

    /// <summary>Draws a bar down the right edge saying where in the table you are.</summary>
    public bool Scrollbar { get; set; }

    /// <summary>How the scrollbar is drawn, when there is one.</summary>
    /// <remarks>
    /// Named and defaulted like every other scrolling widget's. This one used to inline a
    /// dim style at the call site, which is the same decision spelled a fourth way.
    /// </remarks>
    public TuiStyle ScrollbarStyle { get; set; } = new(Attributes: TuiTextAttributes.Dim);

    /// <inheritdoc />
    public override bool IsFocusable { get; } = true;

    /// <inheritdoc />
    public override object? Value => SelectedRow;

    /// <summary>How many rows are visible, once the header and any frame have taken theirs.</summary>
    private int PageSize => Math.Max(1, Bounds.Height - (ShowHeader ? 1 : 0) - FrameRows);

    /// <inheritdoc />
    protected override TuiSize MeasureCore(TuiConstraints constraints)
    {
        var columns = Columns;

        var width = columns.Sum(NaturalWidth)
            + (Separator * Math.Max(0, columns.Count - 1))
            + FrameColumns;

        return constraints.Constrain(new TuiSize(width, _rows.Count + (ShowHeader ? 1 : 0) + FrameRows));
    }

    /// <inheritdoc />
    protected override void ArrangeCore(TuiRect bounds)
    {
        EnsureVisible();
    }

    /// <inheritdoc />
    public override void Draw(TuiSurface outer)
    {
        // The bar runs beside the rows and not beside the header, which is why this one
        // says where it starts and how many rows are actually on screen.
        var surface = TuiScrollbar.Fit(
            outer,
            Scrollbar,
            _offset,
            _rows.Count,
            ScrollbarStyle,
            firstRow: ShowHeader ? 1 : 0,
            visibleRows: PageSize);

        var columns = Visible();
        var widths = Distribute(columns, surface.Width);

        // Cells are drawn inside whatever the grid left them; with no grid that is
        // everything, which is why the two cases are the same code with different insets.
        // Only the outline is taken out of the cells' area. The rule under the header is
        // drawn inside it, and skipped over when the rows start — which is why `top` below
        // counts it rather than this clip.
        var framed = Borders == TuiTableBorders.All;
        var inset = framed ? 2 : 0;

        var cells = surface.Clip(new TuiRect(
            inset,
            framed ? 1 : 0,
            Math.Max(0, surface.Width - (inset * 2)),
            Math.Max(0, surface.Height - (framed ? 2 : 0))));

        DrawGrid(surface, widths);

        var top = 0;

        if (ShowHeader)
        {
            Row(cells, 0, columns, widths, (column, _) => column.Header, HeaderStyle, (_, _) => HeaderStyle);

            // One row for the header, and another for the rule under it when there is one.
            top = Borders == TuiTableBorders.None ? 1 : 2;
        }

        for (var row = top; row < cells.Height; row += 1)
        {
            var index = _offset + row - top;

            if (index >= _rows.Count)
            {
                break;
            }

            var selected = index == _selectedIndex;
            var item = _rows[index];

            Row(
                cells,
                row,
                columns,
                widths,
                (column, _) => Text(column.ValueOf(item)),
                default,
                (column, _) => selected
                    ? SelectedStyle
                    : column.StyleSelector?.Invoke(column.ValueOf(item)) ?? column.Style);
        }

    }

    /// <summary>
    /// The columns from <see cref="ColumnOffset"/> onwards.
    /// </summary>
    /// <remarks>
    /// Scrolling drops columns from the left rather than shifting cells sideways, so every
    /// column on screen is whole and its header still sits above it.
    /// </remarks>
    private IReadOnlyList<TuiColumn> Visible()
    {
        var columns = Columns;

        return ColumnOffset <= 0 || ColumnOffset >= columns.Count
            ? columns
            : [.. columns.Skip(ColumnOffset)];
    }

    /// <summary>Draws whatever grid <see cref="Borders"/> asked for.</summary>
    private void DrawGrid(TuiSurface surface, int[] widths)
    {
        if (Borders == TuiTableBorders.None || surface.Width <= 0 || surface.Height <= 0)
        {
            return;
        }

        // Where a column boundary falls, measured from the first cell column.
        var stops = new List<int>();
        var at = 0;

        for (var index = 0; index < widths.Length - 1; index += 1)
        {
            at += widths[index];
            stops.Add(at + (Separator / 2));
            at += Separator;
        }

        if (Borders == TuiTableBorders.Header)
        {
            // Just the rule under the header: the least a table needs to stop its headings
            // reading as a first row of data.
            if (ShowHeader && surface.Height > 1)
            {
                Rule(surface, 1, 0, surface.Width, stops, Glyphs.Horizontal, Glyphs.Junction);
            }

            return;
        }

        var right = surface.Width - 1;
        var bottom = surface.Height - 1;

        Rule(surface, 0, 1, right, stops.Select(stop => stop + 2), Glyphs.Horizontal, Glyphs.Down);
        Rule(surface, bottom, 1, right, stops.Select(stop => stop + 2), Glyphs.Horizontal, Glyphs.Up);

        surface.DrawText(0, 0, Glyphs.TopLeft, BorderStyle);
        surface.DrawText(right, 0, Glyphs.TopRight, BorderStyle);
        surface.DrawText(0, bottom, Glyphs.BottomLeft, BorderStyle);
        surface.DrawText(right, bottom, Glyphs.BottomRight, BorderStyle);

        for (var row = 1; row < bottom; row += 1)
        {
            surface.DrawText(0, row, Glyphs.Vertical, BorderStyle);
            surface.DrawText(right, row, Glyphs.Vertical, BorderStyle);

            foreach (var stop in stops)
            {
                surface.DrawText(stop + 2, row, Glyphs.Vertical, BorderStyle);
            }
        }

        if (ShowHeader && bottom > 2)
        {
            Rule(surface, 2, 1, right, stops.Select(stop => stop + 2), Glyphs.Horizontal, Glyphs.Junction);
            surface.DrawText(0, 2, Glyphs.Right, BorderStyle);
            surface.DrawText(right, 2, Glyphs.Left, BorderStyle);
        }
    }

    /// <summary>Draws one horizontal rule, broken by a junction at each column boundary.</summary>
    private void Rule(
        TuiSurface surface,
        int row,
        int from,
        int to,
        IEnumerable<int> stops,
        string line,
        string junction)
    {
        var boundaries = stops.ToHashSet();

        for (var column = from; column < to; column += 1)
        {
            surface.DrawText(column, row, boundaries.Contains(column) ? junction : line, BorderStyle);
        }
    }

    /// <inheritdoc />
    public override bool OnInput(TuiInputEvent input)
    {
        if (input.IsMouse)
        {
            return Click(input.Mouse);
        }

        if (!IsFocused)
        {
            return false;
        }

        switch (input.Key.Key)
        {
            case ConsoleKey.UpArrow: SelectedIndex -= 1; return true;
            case ConsoleKey.DownArrow: SelectedIndex += 1; return true;
            case ConsoleKey.PageUp: SelectedIndex -= PageSize; return true;
            case ConsoleKey.PageDown: SelectedIndex += PageSize; return true;
            case ConsoleKey.Home: SelectedIndex = 0; return true;
            case ConsoleKey.End: SelectedIndex = _rows.Count - 1; return true;

            // Left and right move along the columns, which is the only thing they could
            // mean in a grid and the only way to reach a column that does not fit.
            case ConsoleKey.LeftArrow: ScrollColumns(-1); return true;
            case ConsoleKey.RightArrow: ScrollColumns(1); return true;

            case ConsoleKey.Enter:
                // With nothing listening, activation belongs to whatever surrounds this.
                if (Activated is null)
                {
                    return false;
                }

                Activated(SelectedRow);
                return true;

            default:
                return false;
        }
    }

    private bool Click(TuiMouseEvent mouse)
    {
        if (!Bounds.Contains(mouse.Column, mouse.Row))
        {
            return false;
        }

        if (mouse.Action == TuiMouseAction.Scroll)
        {
            SelectedIndex += mouse.Button == TuiMouseButton.ScrollUp ? -1 : 1;
            return true;
        }

        if (mouse.Action != TuiMouseAction.Press || mouse.Button != TuiMouseButton.Left)
        {
            return false;
        }

        var row = mouse.Row
            - Bounds.Top
            - (ShowHeader ? 1 : 0)
            - (Borders == TuiTableBorders.All ? (ShowHeader ? 2 : 1) : Borders == TuiTableBorders.Header && ShowHeader ? 1 : 0);

        // The header is not a row to land on.
        if (row < 0)
        {
            return true;
        }

        SelectedIndex = _offset + row;
        return true;
    }

    /// <summary>Draws one row of cells, each clipped and aligned in its column.</summary>
    private void Row(
        TuiSurface surface,
        int row,
        IReadOnlyList<TuiColumn> columns,
        int[] widths,
        Func<TuiColumn, int, string> text,
        TuiStyle fallback,
        Func<TuiColumn, int, TuiStyle> style)
    {
        var column = 0;

        for (var index = 0; index < columns.Count && column < surface.Width; index += 1)
        {
            var width = widths[index];

            if (width > 0)
            {
                var cell = Align(text(columns[index], index), width, columns[index].Align);
                var cellStyle = style(columns[index], index);

                surface.DrawText(column, row, cell, cellStyle.IsDefault ? fallback : cellStyle, width);
            }

            column += width + Separator;
        }
    }

    /// <summary>Divides the available width between the columns.</summary>
    /// <remarks>
    /// The same rules a stack uses, because a column and a pane are the same question. A
    /// column with no opinion is auto, which is as wide as the widest thing in it.
    /// </remarks>
    private int[] Distribute(IReadOnlyList<TuiColumn> columns, int available)
    {
        var widths = new int[columns.Count];
        var remaining = Math.Max(0, available - (Separator * Math.Max(0, columns.Count - 1)) - FrameColumns);
        var total = remaining;
        var weight = 0;

        for (var index = 0; index < columns.Count; index += 1)
        {
            var length = columns[index].Width;

            var wanted = length.Kind switch
            {
                TuiLengthKind.Fixed => length.Value,
                TuiLengthKind.Fraction => total * length.Value / length.Divisor,
                TuiLengthKind.Auto => NaturalWidth(columns[index]),
                _ => -1,
            };

            if (wanted < 0)
            {
                weight += length.Value;
                continue;
            }

            widths[index] = Math.Clamp(length.Clamp(wanted), 0, remaining);
            remaining -= widths[index];
        }

        if (weight == 0)
        {
            return widths;
        }

        var share = Math.Max(0, remaining);
        var handedOut = 0;
        var last = -1;

        for (var index = 0; index < columns.Count; index += 1)
        {
            var length = columns[index].Width;

            if (length.Kind != TuiLengthKind.Star)
            {
                continue;
            }

            widths[index] = length.Clamp(share * length.Value / weight);
            handedOut += widths[index];

            if (length is { Minimum: null, Maximum: null })
            {
                last = index;
            }
        }

        if (last >= 0)
        {
            widths[last] = Math.Max(0, widths[last] + share - handedOut);
        }

        return widths;
    }

    /// <summary>How wide a column would like to be: its header, or its widest cell.</summary>
    /// <summary>Whether every column fits in the width the table was given.</summary>
    public bool AllColumnsFit
    {
        get
        {
            var columns = Columns;

            return columns.Sum(NaturalWidth)
                + (Separator * Math.Max(0, columns.Count - 1))
                + FrameColumns <= Bounds.Width;
        }
    }

    private int NaturalWidth(TuiColumn column)
    {
        var width = ShowHeader ? TextMeasure.MeasureWidth(column.Header) : 0;

        foreach (var row in _rows)
        {
            width = Math.Max(width, TextMeasure.MeasureWidth(Text(column.ValueOf(row))));
        }

        return width;
    }

    /// <summary>
    /// How columns are derived when nobody said what they are.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Installed by the host, because the shell has already decided which properties of an
    /// object are worth showing and a table that decides again gives a different answer.
    /// Reflection over a <c>FileInfo</c> yields forty-nine columns; <c>ls</c> shows four,
    /// and <c>ls | tui table</c> should show the same four.
    /// </para>
    /// <para>
    /// A host that installs nothing gets the reflection fallback, which is right for a
    /// test and for anyone using the widget outside the shell.
    /// </para>
    /// </remarks>
    public static Func<object, IReadOnlyList<TuiColumn>?>? ColumnSource { get; set; }

    /// <summary>Takes the columns from the first row, when nobody said what they are.</summary>
    private IReadOnlyList<TuiColumn> DeriveColumns()
    {
        var first = _rows.FirstOrDefault(row => row is not null);

        if (first is null)
        {
            return [];
        }

        if (ColumnSource?.Invoke(first) is { Count: > 0 } supplied)
        {
            return supplied;
        }

        if (ShellRecordUtilities.TryGetFields(first, out var fields) && fields.Count > 0)
        {
            return [.. fields.Select(field => new TuiColumn(field.Key))];
        }

        var properties = first.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetIndexParameters().Length == 0 && property.CanRead)
            .ToArray();

        return properties.Length > 0
            ? [.. properties.Select(property => new TuiColumn(property.Name))]
            : [new TuiColumn(string.Empty) { Selector = row => row }];
    }

    private void EnsureVisible()
    {
        var page = PageSize;

        _offset = Math.Clamp(_offset, 0, Math.Max(0, _rows.Count - page));

        if (_selectedIndex < _offset)
        {
            _offset = _selectedIndex;
        }
        else if (_selectedIndex >= _offset + page)
        {
            _offset = _selectedIndex - page + 1;
        }
    }

    private static string Text(object? value) => value?.ToString() ?? string.Empty;

    private static string Align(string text, int width, TuiAlignment alignment)
    {
        var measured = TextMeasure.MeasureWidth(text);

        // An ellipsis where a value was cut, so a reader can tell a truncated cell from one
        // that merely happens to be exactly that wide. Not worth it at one column, where
        // the ellipsis would be the whole cell.
        if (measured > width)
        {
            return width <= 1
                ? TextMeasure.Truncate(text, width)
                : TextMeasure.Truncate(text, width - 1) + "…";
        }

        if (measured == width)
        {
            return text;
        }

        var slack = width - measured;

        return alignment switch
        {
            TuiAlignment.Right => new string(' ', slack) + text,
            TuiAlignment.Center => new string(' ', slack / 2) + text + new string(' ', slack - (slack / 2)),
            _ => text,
        };
    }
}
