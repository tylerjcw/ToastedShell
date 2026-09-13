using System.Reflection;
using Tosh.Runtime;
using Tosh.Tui.Rendering;

namespace Tosh.Tui.Widgets;

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

    /// <summary>How the header row is drawn.</summary>
    public TuiStyle HeaderStyle { get; set; } = new(Attributes: TuiTextAttributes.Bold);

    /// <summary>How the selected row is drawn.</summary>
    public TuiStyle SelectedStyle { get; set; } = new(Attributes: TuiTextAttributes.Reverse);

    /// <summary>Blank columns between one column and the next.</summary>
    public int Gap { get; set; } = 1;

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

    /// <summary>Raised when the selection moves.</summary>
    public Action<int>? SelectionChanged { get; set; }

    /// <summary>Raised when a row is chosen with Enter.</summary>
    public Action<object?>? Activated { get; set; }

    /// <summary>Draws a bar down the right edge saying where in the table you are.</summary>
    public bool Scrollbar { get; set; }

    /// <inheritdoc />
    public override bool IsFocusable { get; } = true;

    /// <inheritdoc />
    public override object? Value => SelectedRow;

    /// <summary>How many rows are visible, once the header has taken its row.</summary>
    private int PageSize => Math.Max(1, Bounds.Height - (ShowHeader ? 1 : 0));

    /// <inheritdoc />
    public override TuiSize Measure(TuiConstraints constraints)
    {
        var columns = Columns;
        var width = columns.Sum(column => NaturalWidth(column)) + (Gap * Math.Max(0, columns.Count - 1));

        return constraints.Constrain(new TuiSize(width, _rows.Count + (ShowHeader ? 1 : 0)));
    }

    /// <inheritdoc />
    public override void Arrange(TuiRect bounds)
    {
        base.Arrange(bounds);
        EnsureVisible();
    }

    /// <inheritdoc />
    public override void Draw(TuiSurface outer)
    {
        var surface = Scrollbar && _rows.Count > PageSize
            ? outer.Clip(new TuiRect(0, 0, Math.Max(0, outer.Width - 1), outer.Height))
            : outer;

        var columns = Columns;
        var widths = Distribute(columns, surface.Width);
        var top = 0;

        if (ShowHeader)
        {
            Row(surface, 0, columns, widths, (column, _) => column.Header, HeaderStyle, (_, _) => HeaderStyle);
            top = 1;
        }

        for (var row = top; row < surface.Height; row += 1)
        {
            var index = _offset + row - top;

            if (index >= _rows.Count)
            {
                break;
            }

            var selected = index == _selectedIndex;
            var item = _rows[index];

            Row(
                surface,
                row,
                columns,
                widths,
                (column, _) => Text(column.ValueOf(item)),
                default,
                (column, _) => selected
                    ? SelectedStyle
                    : column.StyleSelector?.Invoke(column.ValueOf(item)) ?? column.Style);
        }

        if (Scrollbar)
        {
            TuiScrollbar.DrawVertical(
                outer.Clip(new TuiRect(0, top, outer.Width, Math.Max(0, outer.Height - top))),
                _offset,
                _rows.Count,
                new TuiStyle(Attributes: TuiTextAttributes.Dim),
                new TuiStyle(Attributes: TuiTextAttributes.Dim));
        }
    }

    /// <inheritdoc />
    public override bool OnInput(TuiInputEvent input)
    {
        if (!input.IsKey)
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

        var row = mouse.Row - Bounds.Top - (ShowHeader ? 1 : 0);

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

            column += width + Gap;
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
        var remaining = Math.Max(0, available - (Gap * Math.Max(0, columns.Count - 1)));
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
    private int NaturalWidth(TuiColumn column)
    {
        var width = ShowHeader ? TuiTextMeasure.MeasureWidth(column.Header) : 0;

        foreach (var row in _rows)
        {
            width = Math.Max(width, TuiTextMeasure.MeasureWidth(Text(column.ValueOf(row))));
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
        var measured = TuiTextMeasure.MeasureWidth(text);

        // An ellipsis where a value was cut, so a reader can tell a truncated cell from one
        // that merely happens to be exactly that wide. Not worth it at one column, where
        // the ellipsis would be the whole cell.
        if (measured > width)
        {
            return width <= 1
                ? TuiTextMeasure.Truncate(text, width)
                : TuiTextMeasure.Truncate(text, width - 1) + "…";
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
