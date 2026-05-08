using System.Text;
using Tosh.Cli.Tui;
using Tosh.Runtime;

namespace Tosh.Cli;

/// <summary>
/// Renders pipeline rows as they arrive, drawing each row to the terminal immediately.
/// When a new row requires wider columns, rewrites the entire table from the top border
/// provided it is still on screen; otherwise locks widths and clips.
/// Falls back to batch rendering if the first value does not resolve to table columns.
/// </summary>
internal sealed class StreamingTableSink : IDisplaySink
{
    private readonly ToshRuntime _runtime;
    private readonly DisplayEngine _display;
    private readonly DisplayRenderOptions _options;
    private readonly ToshTableThemeConfig _theme;
    private readonly InlineTablePlan.BoxChars _box;

    private IReadOnlyList<DisplayTableColumn>? _columns;
    private int[]? _widths;
    private readonly List<string[]> _renderedCells = [];
    private int _renderedRowCount;
    private bool _widthsLocked;
    private bool _isTable;

    private readonly List<object?> _fallbackValues = [];

    public StreamingTableSink(ToshRuntime runtime)
    {
        _runtime = runtime;
        _display = runtime.Display;
        _options = ConsoleDisplay.CreateRenderOptions(runtime);
        _theme = runtime.Config.Theme.Tables;
        _box = InlineTablePlan.GetBoxCharacters(_theme.BoxStyle);
    }

    public async ValueTask EmitAsync(object? value, CancellationToken cancellationToken = default)
    {
        if (!_isTable)
        {
            if (value is not null && _columns is null)
            {
                if (_display.TryBuildStreamingColumns(value, _options, out var cols) && cols.Count > 0)
                {
                    _columns = cols;
                    _widths = InitializeWidths(cols);
                    _isTable = true;

                    var cells = FormatCells(value);
                    UpdateWidths(cells);
                    _renderedCells.Add(cells);
                    _renderedRowCount = 1;
                    await DrawInitialTableAsync();
                    return;
                }
            }

            _fallbackValues.Add(value);
            return;
        }

        // In table mode but received a null — skip it.
        if (value is null)
            return;

        var newCells = FormatCells(value);
        var expansionNeeded = WouldExpand(newCells);

        _renderedCells.Add(newCells);
        _renderedRowCount++;

        if (expansionNeeded && !_widthsLocked)
        {
            RecalculateWidths();

            // _renderedRowCount-1 rows are currently drawn on screen.
            // Lines above cursor: (renderedRowCount-1) data rows + top border + header + mid border + bottom border = renderedRowCount+3
            var linesToGoUp = _renderedRowCount + 3;
            if (Console.CursorTop - linesToGoUp >= 0)
            {
                Console.Write($"\x1b[{linesToGoUp}A\r");
                await DrawFullTableAsync();
                return;
            }

            _widthsLocked = true;
        }

        // Overwrite the bottom border in place, then draw new row + new bottom border.
        Console.Write("\x1b[1A\r");
        await WriteRowAsync(newCells);
        await WriteBottomBorderAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_isTable)
        {
            _runtime.ClearDisplaySelections();
            return;
        }

        try
        {
            if (TuiRequestProbe.IsTuiRequestBatch(_fallbackValues) &&
                TuiRequestDispatcher.TryHandle(_fallbackValues, _runtime, out var outcomeValues))
            {
                if (outcomeValues is { Count: > 0 })
                {
                    var rendered = _runtime.Display.RenderMany(outcomeValues, _options);
                    await ConsoleDisplay.WriteRenderedAsync(rendered, _runtime);
                }

                return;
            }

            var rendered2 = _runtime.Display.RenderMany(_fallbackValues, _options);
            await ConsoleDisplay.WriteRenderedAsync(rendered2, _runtime);
        }
        finally
        {
            _runtime.ClearDisplaySelections();
        }
    }

    private async Task DrawInitialTableAsync()
    {
        await Console.Out.WriteLineAsync(StyledBorder(BuildBorder(_box.TopLeft, _box.TopMiddle, _box.TopRight)));
        await WriteHeaderAsync();
        await Console.Out.WriteLineAsync(StyledBorder(BuildBorder(_box.MiddleLeft, _box.MiddleMiddle, _box.MiddleRight)));
        await WriteRowAsync(_renderedCells[0]);
        await WriteBottomBorderAsync();
    }

    private async Task DrawFullTableAsync()
    {
        await Console.Out.WriteLineAsync(StyledBorder(BuildBorder(_box.TopLeft, _box.TopMiddle, _box.TopRight)));
        await WriteHeaderAsync();
        await Console.Out.WriteLineAsync(StyledBorder(BuildBorder(_box.MiddleLeft, _box.MiddleMiddle, _box.MiddleRight)));
        foreach (var cells in _renderedCells)
            await WriteRowAsync(cells);
        await WriteBottomBorderAsync();
    }

    private Task WriteHeaderAsync()
    {
        var sb = new StringBuilder();
        var vertical = _theme.Border.Apply(_box.Vertical.ToString()).ToAnsi();
        sb.Append(vertical);

        for (var i = 0; i < _columns!.Count; i++)
        {
            var clipped = InlineTablePlan.ClipCell(_columns[i].Header, _widths![i]);
            var padded = _columns[i].Alignment == DisplayTableAlignment.Right
                ? InlineTablePlan.PadLeft(clipped, _widths[i])
                : InlineTablePlan.PadRight(clipped, _widths[i]);
            var styled = _columns[i].UseHeaderTheme
                ? _theme.Header.Apply(padded).ToAnsi()
                : padded;
            sb.Append(' ').Append(styled).Append(' ').Append(vertical);
        }

        return Console.Out.WriteLineAsync(sb.ToString());
    }

    private Task WriteRowAsync(string[] cells)
    {
        var sb = new StringBuilder();
        var vertical = _theme.Border.Apply(_box.Vertical.ToString()).ToAnsi();
        sb.Append(vertical);

        for (var i = 0; i < _columns!.Count && i < cells.Length; i++)
        {
            var clipped = InlineTablePlan.ClipCell(cells[i], _widths![i]);
            var padded = _columns[i].Alignment == DisplayTableAlignment.Right
                ? InlineTablePlan.PadLeft(clipped, _widths[i])
                : InlineTablePlan.PadRight(clipped, _widths[i]);

            if (i == 0 && _columns[i].UseIndexTheme)
                padded = _theme.Index.Apply(padded).ToAnsi();

            sb.Append(' ').Append(padded).Append(' ').Append(vertical);
        }

        return Console.Out.WriteLineAsync(sb.ToString());
    }

    private Task WriteBottomBorderAsync()
        => Console.Out.WriteLineAsync(StyledBorder(BuildBorder(_box.BottomLeft, _box.BottomMiddle, _box.BottomRight)));

    private string BuildBorder(char left, char center, char right)
        => InlineTablePlan.BuildBorder(_widths!, left, center, right, _box.Horizontal);

    private string StyledBorder(string border)
        => _theme.Border.Apply(border).ToAnsi();

    private string[] FormatCells(object row)
    {
        var cells = new string[_columns!.Count];
        for (var i = 0; i < _columns.Count; i++)
        {
            var raw = _columns[i].ValueAccessor(row);
            cells[i] = _display.FormatStreamingCellValue(raw, _options);
        }
        return cells;
    }

    private int[] InitializeWidths(IReadOnlyList<DisplayTableColumn> columns)
    {
        var widths = new int[columns.Count];
        for (var i = 0; i < columns.Count; i++)
            widths[i] = Math.Max(StyledText.GetVisibleLength(columns[i].Header), columns[i].MinWidth);
        ClampToMaxWidth(widths);
        return widths;
    }

    private bool WouldExpand(string[] cells)
    {
        for (var i = 0; i < _columns!.Count && i < cells.Length; i++)
        {
            var cellWidth = StyledText.GetVisibleLength(cells[i]);
            var needed = Math.Min(Math.Max(cellWidth, _columns[i].MinWidth), _columns[i].MaxWidth);
            if (needed > _widths![i])
                return true;
        }
        return false;
    }

    private void UpdateWidths(string[] cells)
    {
        for (var i = 0; i < _columns!.Count && i < cells.Length; i++)
        {
            var cellWidth = StyledText.GetVisibleLength(cells[i]);
            var needed = Math.Min(Math.Max(cellWidth, _columns[i].MinWidth), _columns[i].MaxWidth);
            if (needed > _widths![i])
                _widths[i] = needed;
        }
        ClampToMaxWidth(_widths!);
    }

    private void RecalculateWidths()
    {
        _widths = InitializeWidths(_columns!);
        foreach (var cells in _renderedCells)
            UpdateWidths(cells);
    }

    /// <summary>
    /// Shrinks column widths in-place so that the rendered table fits within
    /// <see cref="DisplayRenderOptions.MaxWidth"/>. Mirrors the buffered-table
    /// behaviour in <c>DisplayEngine.BuildVisibleColumns</c>: widest columns
    /// shrink first, never below their per-column <c>MinWidth</c>.
    /// </summary>
    private void ClampToMaxWidth(int[] widths)
    {
        if (_options.MaxWidth is not int maxWidth || maxWidth <= 0 || _columns is null)
            return;

        // Total rendered width = sum(width + 2 padding) + (count + 1) borders.
        static int Total(int[] w, int count) => w.Take(count).Sum() + 3 * count + 1;

        while (Total(widths, _columns.Count) > maxWidth)
        {
            var excess = Total(widths, _columns.Count) - maxWidth;
            var shrinkableIndex = -1;
            var shrinkableSlack = 0;
            for (var i = 0; i < _columns.Count; i++)
            {
                var slack = widths[i] - _columns[i].MinWidth;
                if (slack > shrinkableSlack)
                {
                    shrinkableSlack = slack;
                    shrinkableIndex = i;
                }
            }

            if (shrinkableIndex < 0)
                break;

            var reduction = Math.Min(shrinkableSlack, excess);
            widths[shrinkableIndex] -= reduction;
        }
    }
}
