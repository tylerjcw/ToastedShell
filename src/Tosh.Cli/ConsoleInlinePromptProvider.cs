using System.Text;
using System.Text.RegularExpressions;
using Tosh.Core;

namespace Tosh.Cli;

/// <summary>
/// Inline prompt provider that renders prompts within the terminal output flow
/// using ANSI escape codes and box-drawn table rendering matching the shell's display engine.
/// </summary>
internal sealed partial class ConsoleInlinePromptProvider : IInlinePromptProvider
{
    private const string ClearLine = "\x1b[2K\r";
    private const string HideCursor = "\x1b[?25l";
    private const string ShowCursor = "\x1b[?25h";
    private const int SelectorColumnWidth = 1;

    private readonly ToshRuntime? _runtime;
    private readonly ObjectFormatter? _formatter;
    private readonly DisplayEngine? _display;

    [GeneratedRegex(@"\x1b\[[0-9;]*[a-zA-Z]")]
    private static partial Regex AnsiEscapePattern();

    public ConsoleInlinePromptProvider(ToshRuntime? runtime = null)
    {
        _runtime = runtime;
        _formatter = runtime?.Formatter;
        _display = runtime?.Display;
    }

    public IReadOnlyList<object?>? Pick(IReadOnlyList<object?> items, string? prompt = null, string? displayProperty = null, bool multiSelect = false, int pageSize = 10)
    {
        if (items.Count == 0)
        {
            return null;
        }

        var layout = BuildTableLayout(items, displayProperty, pageSize);
        var selected = new HashSet<int>();
        var cursor = 0;
        var viewOffset = 0;
        var visibleCount = Math.Min(pageSize, items.Count);

        // topBorder + titleRow + spanToCol + header + colSep + N data + colToSpan + helpRow + bottomBorder
        var totalLines = visibleCount + 8;
        var firstRender = true;

        var promptText = prompt ?? (multiSelect ? "Pick items:" : "Pick an item:");
        var help = multiSelect
            ? "↑/↓ navigate  space toggle  enter confirm  esc cancel"
            : "↑/↓ navigate  enter select  esc cancel";

        Console.Write(HideCursor);

        try
        {
            while (true)
            {
                if (cursor < viewOffset)
                {
                    viewOffset = cursor;
                }

                if (cursor >= viewOffset + visibleCount)
                {
                    viewOffset = cursor - visibleCount + 1;
                }

                if (firstRender)
                {
                    ReserveLines(totalLines);
                    MoveUp(totalLines);
                    firstRender = false;
                }
                else
                {
                    Console.Write("\r");
                    MoveUp(totalLines - 1);
                }

                // Top border
                Console.Write(ClearLine);
                Console.WriteLine(BuildTopSpanBorder(layout));

                // Title row
                Console.Write(ClearLine);
                var titleContent = multiSelect
                    ? $"{promptText} ({selected.Count} selected)"
                    : promptText;
                Console.WriteLine(BuildSpanRow(titleContent, layout, layout.TitleStyle));

                // Span-to-columns separator
                Console.Write(ClearLine);
                Console.WriteLine(BuildSpanToColumnsSeparator(layout));

                // Header row
                Console.Write(ClearLine);
                Console.WriteLine(BuildHeaderRow(layout));

                // Column separator
                Console.Write(ClearLine);
                Console.WriteLine(BuildColumnSeparator(layout));

                // Data rows
                for (var i = 0; i < visibleCount; i++)
                {
                    Console.Write(ClearLine);

                    if (i < Math.Min(visibleCount, items.Count - viewOffset))
                    {
                        var rowIndex = viewOffset + i;
                        var isCursor = rowIndex == cursor;
                        var isSelected = selected.Contains(rowIndex);
                        var marker = GetMarker(isCursor, isSelected, multiSelect);
                        Console.Write(BuildDataRow(layout, rowIndex, marker, isCursor));
                    }
                    else
                    {
                        Console.Write(BuildEmptyRow(layout));
                    }

                    Console.WriteLine();
                }

                // Columns-to-span separator
                Console.Write(ClearLine);
                Console.WriteLine(BuildColumnsToSpanSeparator(layout));

                // Help row
                Console.Write(ClearLine);
                Console.WriteLine(BuildSpanRow(help, layout, layout.FooterStyle));

                // Bottom border
                Console.Write(ClearLine);
                Console.Write(BuildBottomSpanBorder(layout));

                var key = Console.ReadKey(intercept: true);

                switch (key.Key)
                {
                    case ConsoleKey.UpArrow or ConsoleKey.K:
                        if (cursor > 0) cursor--;
                        break;
                    case ConsoleKey.DownArrow or ConsoleKey.J:
                        if (cursor < items.Count - 1) cursor++;
                        break;
                    case ConsoleKey.Home:
                        cursor = 0;
                        break;
                    case ConsoleKey.End:
                        cursor = items.Count - 1;
                        break;
                    case ConsoleKey.PageUp:
                        cursor = Math.Max(0, cursor - visibleCount);
                        break;
                    case ConsoleKey.PageDown:
                        cursor = Math.Min(items.Count - 1, cursor + visibleCount);
                        break;
                    case ConsoleKey.Spacebar when multiSelect:
                        if (!selected.Remove(cursor))
                        {
                            selected.Add(cursor);
                        }

                        break;
                    case ConsoleKey.Enter:
                        CleanupLines(totalLines);
                        Console.Write(ShowCursor);

                        if (multiSelect)
                        {
                            return selected.Count > 0
                                ? selected.OrderBy(i => i).Select(i => items[i]).ToArray()
                                : [items[cursor]];
                        }

                        return [items[cursor]];
                    case ConsoleKey.Escape:
                        CleanupLines(totalLines);
                        Console.Write(ShowCursor);
                        return null;
                }
            }
        }
        catch
        {
            Console.Write(ShowCursor);
            throw;
        }
    }

    public bool? Confirm(string message, bool defaultValue = true)
    {
        var tableTheme = _runtime?.Config.Theme.Tables ?? new ToshTableThemeConfig();
        var tuiTheme = _runtime?.Config.Theme.Tui ?? new ToshTuiThemeConfig();
        var box = InlineTablePlan.GetBoxCharacters(tableTheme.BoxStyle);
        var borderStyle = tableTheme.Border;
        var titleStyle = tuiTheme.Title;

        var hint = defaultValue ? "Y/n" : "y/N";
        var contentText = $" {message} [{hint}] ";
        var contentWidth = Math.Max(contentText.Length + 12, 30);

        var v = borderStyle.Apply(box.Vertical.ToString()).ToAnsi();

        // Top border with "Confirm" title
        var titleText = " Confirm ";
        var topTrailing = Math.Max(0, contentWidth - 1 - titleText.Length);
        Console.Write(
            borderStyle.Apply($"{box.TopLeft}{box.Horizontal}").ToAnsi()
            + titleStyle.Apply(titleText).ToAnsi()
            + borderStyle.Apply($"{new string(box.Horizontal, topTrailing)}{box.TopRight}").ToAnsi());
        Console.WriteLine();

        // Content row (partial — waiting for input)
        var paddedContent = InlineTablePlan.PadRight(contentText, contentWidth);
        Console.Write($"{v}{paddedContent}{v} ");
        Console.Out.Flush();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            string answer;
            int answerLen;
            bool? result;

            switch (key.Key)
            {
                case ConsoleKey.Y:
                    answer = "\x1b[32myes\x1b[0m";
                    answerLen = 3;
                    result = true;
                    break;
                case ConsoleKey.N:
                    answer = "\x1b[33mno\x1b[0m";
                    answerLen = 2;
                    result = false;
                    break;
                case ConsoleKey.Enter:
                    answer = defaultValue ? "\x1b[32myes\x1b[0m" : "\x1b[33mno\x1b[0m";
                    answerLen = defaultValue ? 3 : 2;
                    result = defaultValue;
                    break;
                case ConsoleKey.Escape:
                    answer = "\x1b[2mcancelled\x1b[0m";
                    answerLen = 9;
                    result = null;
                    break;
                default:
                    continue;
            }

            // Rewrite content row with answer
            Console.Write("\r");
            var fullContent = $"{contentText}{answer}";
            var fullPlain = contentText.Length + answerLen;
            var remaining = Math.Max(0, contentWidth - fullPlain);
            Console.Write($"{v}{contentText}{answer}{new string(' ', remaining)}{v}");
            Console.WriteLine();

            // Bottom border
            var bottomLine = borderStyle.Apply(
                $"{box.BottomLeft}{new string(box.Horizontal, contentWidth)}{box.BottomRight}").ToAnsi();
            Console.WriteLine(bottomLine);

            return result;
        }
    }

    public string? Input(string? prompt = null, string? defaultValue = null, bool password = false)
    {
        var tableTheme = _runtime?.Config.Theme.Tables ?? new ToshTableThemeConfig();
        var tuiTheme = _runtime?.Config.Theme.Tui ?? new ToshTuiThemeConfig();
        var box = InlineTablePlan.GetBoxCharacters(tableTheme.BoxStyle);
        var borderStyle = tableTheme.Border;
        var titleStyle = tuiTheme.Title;

        var label = prompt ?? ">";
        var titleText = $" {label} ";
        var consoleWidth = TryGetConsoleWidth() ?? 80;
        var minInnerWidth = Math.Max(titleText.Length + 4, 20);

        var v = borderStyle.Apply(box.Vertical.ToString()).ToAnsi();

        var defaultPrefix = "";
        var defaultPrefixLen = 0;

        if (defaultValue is not null && !password)
        {
            defaultPrefix = $"\x1b[2m({defaultValue})\x1b[0m ";
            defaultPrefixLen = defaultValue.Length + 3; // "(" + value + ") "
        }

        var buffer = new StringBuilder(password ? string.Empty : (defaultValue ?? string.Empty));
        var cursorPos = buffer.Length;
        var totalLines = 3;
        var firstRender = true;
        var finished = false;
        string? statusText = null;

        try
        {
            while (true)
            {
                // Calculate dynamic inner width
                var contentLen = 1 + defaultPrefixLen + (password ? buffer.Length : buffer.Length) + 1; // " " + prefix + text + " "
                var innerWidth = Math.Max(minInnerWidth, contentLen);
                innerWidth = Math.Min(innerWidth, consoleWidth - 2); // leave room for left+right borders

                // Position
                if (firstRender)
                {
                    ReserveLines(totalLines);
                    MoveUp(totalLines);
                    firstRender = false;
                }
                else
                {
                    Console.Write("\r");
                    MoveUp(totalLines - 1);
                }

                // Top border with title
                Console.Write(ClearLine);
                var topTrailing = Math.Max(0, innerWidth - 1 - titleText.Length);
                Console.Write(
                    borderStyle.Apply($"{box.TopLeft}{box.Horizontal}").ToAnsi()
                    + titleStyle.Apply(titleText).ToAnsi()
                    + borderStyle.Apply($"{new string(box.Horizontal, topTrailing)}{box.TopRight}").ToAnsi());
                Console.WriteLine();

                // Content row with both borders
                Console.Write(ClearLine);
                var displayText = password ? new string('*', buffer.Length) : buffer.ToString();
                var contentText = $" {(defaultPrefixLen > 0 ? $"\x1b[2m({defaultValue})\x1b[0m " : "")}{displayText}";
                var contentVisibleLen = 1 + defaultPrefixLen + (password ? buffer.Length : buffer.Length);

                if (statusText is not null)
                {
                    contentText += statusText;
                    contentVisibleLen += StripAnsi(statusText).Length;
                }

                var contentPadding = Math.Max(0, innerWidth - contentVisibleLen);
                Console.Write($"{v}{contentText}{new string(' ', contentPadding)}{v}");
                Console.WriteLine();

                // Bottom border
                Console.Write(ClearLine);
                Console.Write(borderStyle.Apply(
                    $"{box.BottomLeft}{new string(box.Horizontal, innerWidth)}{box.BottomRight}").ToAnsi());

                if (finished)
                {
                    Console.WriteLine();
                    break;
                }

                // Position cursor on the content row at the right spot
                // Move up 1 line (from bottom border to content row), then set column
                Console.Write("\x1b[1A");
                var cursorCol = 1 + 1 + defaultPrefixLen + (password ? buffer.Length : cursorPos) + 1; // border + space + prefix + pos, 1-indexed
                Console.Write($"\x1b[{cursorCol}G");
                Console.Out.Flush();

                var key = Console.ReadKey(intercept: true);

                switch (key.Key)
                {
                    case ConsoleKey.Enter:
                        finished = true;
                        // Move back down to bottom border line for final redraw
                        Console.Write("\x1b[1B");
                        break;

                    case ConsoleKey.Escape:
                        statusText = " \x1b[2mcancelled\x1b[0m";
                        finished = true;
                        Console.Write("\x1b[1B");
                        break;

                    case ConsoleKey.Backspace:
                        if (cursorPos > 0)
                        {
                            buffer.Remove(cursorPos - 1, 1);
                            cursorPos--;
                        }

                        break;

                    case ConsoleKey.Delete:
                        if (cursorPos < buffer.Length)
                        {
                            buffer.Remove(cursorPos, 1);
                        }

                        break;

                    case ConsoleKey.LeftArrow:
                        if (cursorPos > 0) cursorPos--;
                        break;

                    case ConsoleKey.RightArrow:
                        if (cursorPos < buffer.Length) cursorPos++;
                        break;

                    case ConsoleKey.Home:
                        cursorPos = 0;
                        break;

                    case ConsoleKey.End:
                        cursorPos = buffer.Length;
                        break;

                    default:
                        if (!password && key.KeyChar >= ' ')
                        {
                            buffer.Insert(cursorPos, key.KeyChar);
                            cursorPos++;
                        }
                        else if (password && key.KeyChar >= ' ')
                        {
                            buffer.Append(key.KeyChar);
                            cursorPos = buffer.Length;
                        }

                        break;
                }

                // After reading key on content row, move down to bottom border so redraw loop is at correct position
                if (!finished)
                {
                    Console.Write("\x1b[1B");
                }
            }
        }
        catch
        {
            Console.Write(ShowCursor);
            throw;
        }

        if (statusText is not null)
        {
            return null;
        }

        var result = buffer.ToString();
        return result.Length == 0 && defaultValue is not null ? defaultValue : result;
    }

    public IReadOnlyList<object?>? Filter(IReadOnlyList<object?> items, string? prompt = null, string? displayProperty = null, bool multiSelect = false, int pageSize = 10)
    {
        if (items.Count == 0)
        {
            return null;
        }

        var layout = BuildTableLayout(items, displayProperty, pageSize);
        var searchBuffer = new StringBuilder();
        var filtered = Enumerable.Range(0, items.Count).ToList();
        var selected = new HashSet<int>();
        var cursor = 0;
        var viewOffset = 0;
        var visibleCount = Math.Min(pageSize, items.Count);
        var labels = FormatLabels(items, displayProperty);

        // topBorder + titleRow + spanToCol + header + colSep + N data + colToSpan + helpRow + bottomBorder
        var totalLines = visibleCount + 8;
        var firstRender = true;

        try
        {
            while (true)
            {
                // Recompute filter
                if (searchBuffer.Length > 0)
                {
                    var term = searchBuffer.ToString();
                    filtered = Enumerable.Range(0, items.Count)
                        .Where(i => labels[i].Contains(term, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }
                else
                {
                    filtered = Enumerable.Range(0, items.Count).ToList();
                }

                if (cursor >= filtered.Count)
                {
                    cursor = Math.Max(0, filtered.Count - 1);
                }

                var currentVisible = Math.Min(visibleCount, filtered.Count);

                if (cursor < viewOffset)
                {
                    viewOffset = cursor;
                }

                if (cursor >= viewOffset + currentVisible)
                {
                    viewOffset = cursor - currentVisible + 1;
                }

                if (firstRender)
                {
                    ReserveLines(totalLines);
                    MoveUp(totalLines);
                    firstRender = false;
                }
                else
                {
                    Console.Write("\r");
                    MoveUp(totalLines - 1);
                }

                // Top border
                Console.Write(ClearLine);
                Console.WriteLine(BuildTopSpanBorder(layout));

                // Search row
                Console.Write(ClearLine);
                var searchPrompt = prompt ?? "Filter:";
                Console.WriteLine(BuildSearchRow(searchPrompt, searchBuffer.ToString(), filtered.Count, items.Count, layout));

                // Span-to-columns separator
                Console.Write(ClearLine);
                Console.WriteLine(BuildSpanToColumnsSeparator(layout));

                // Header row
                Console.Write(ClearLine);
                Console.WriteLine(BuildHeaderRow(layout));

                // Column separator
                Console.Write(ClearLine);
                Console.WriteLine(BuildColumnSeparator(layout));

                // Data rows
                for (var i = 0; i < visibleCount; i++)
                {
                    Console.Write(ClearLine);

                    if (i < currentVisible)
                    {
                        var itemIndex = filtered[viewOffset + i];
                        var isCursor = (viewOffset + i) == cursor;
                        var isSelected = selected.Contains(itemIndex);
                        var marker = GetMarker(isCursor, isSelected, multiSelect);

                        Console.Write(BuildDataRow(layout, itemIndex, marker, isCursor));
                    }
                    else
                    {
                        Console.Write(BuildEmptyRow(layout));
                    }

                    Console.WriteLine();
                }

                // Columns-to-span separator
                Console.Write(ClearLine);
                Console.WriteLine(BuildColumnsToSpanSeparator(layout));

                // Help row
                Console.Write(ClearLine);
                var help = multiSelect
                    ? "type to filter  ↑/↓ navigate  space toggle  enter confirm  esc cancel"
                    : "type to filter  ↑/↓ navigate  enter select  esc cancel";
                Console.WriteLine(BuildSpanRow(help, layout, layout.FooterStyle));

                // Bottom border
                Console.Write(ClearLine);
                Console.Write(BuildBottomSpanBorder(layout));

                var key = Console.ReadKey(intercept: true);

                switch (key.Key)
                {
                    case ConsoleKey.UpArrow:
                        if (cursor > 0) cursor--;
                        break;
                    case ConsoleKey.DownArrow:
                        if (cursor < filtered.Count - 1) cursor++;
                        break;
                    case ConsoleKey.Spacebar when multiSelect:
                        if (filtered.Count > 0)
                        {
                            var idx = filtered[cursor];

                            if (!selected.Remove(idx))
                            {
                                selected.Add(idx);
                            }
                        }

                        break;
                    case ConsoleKey.Backspace:
                        if (searchBuffer.Length > 0)
                        {
                            searchBuffer.Length--;
                            cursor = 0;
                            viewOffset = 0;
                        }

                        break;
                    case ConsoleKey.Enter:
                        CleanupLines(totalLines);

                        if (filtered.Count == 0)
                        {
                            return null;
                        }

                        if (multiSelect)
                        {
                            return selected.Count > 0
                                ? selected.OrderBy(i => i).Select(i => items[i]).ToArray()
                                : [items[filtered[cursor]]];
                        }

                        return [items[filtered[cursor]]];
                    case ConsoleKey.Escape:
                        CleanupLines(totalLines);
                        return null;
                    default:
                        if (key.KeyChar >= ' ')
                        {
                            searchBuffer.Append(key.KeyChar);
                            cursor = 0;
                            viewOffset = 0;
                        }

                        break;
                }
            }
        }
        catch
        {
            Console.Write(ShowCursor);
            throw;
        }
    }

    // ── Table layout ──────────────────────────────────────────

    private sealed class TableLayout
    {
        public required InlineTablePlan.BoxChars Box { get; init; }
        public required ToshTextStyleConfig BorderStyle { get; init; }
        public required ToshTextStyleConfig HeaderStyle { get; init; }
        public required ToshTextStyleConfig IndexStyle { get; init; }
        public required ToshTextStyleConfig SelectionStyle { get; init; }
        public required ToshTextStyleConfig TitleStyle { get; init; }
        public required ToshTextStyleConfig FooterStyle { get; init; }
        public required ToshTextStyleConfig SearchLabelStyle { get; init; }
        public required ToshTextStyleConfig SearchInputStyle { get; init; }
        public required int[] DataColumnWidths { get; init; }
        public required string[] DataHeaders { get; init; }
        public required DisplayTableAlignment[] Alignments { get; init; }
        public required bool[] IsIndexColumn { get; init; }
        public required string[][] DataRows { get; init; }
        public required int TotalTableWidth { get; init; }
    }

    private TableLayout BuildTableLayout(IReadOnlyList<object?> items, string? displayProperty, int pageSize)
    {
        var tableTheme = _runtime?.Config.Theme.Tables ?? new ToshTableThemeConfig();
        var tuiTheme = _runtime?.Config.Theme.Tui ?? new ToshTuiThemeConfig();

        // Reserve width for selector column + its border/padding
        var consoleWidth = TryGetConsoleWidth();
        var selectorOverhead = SelectorColumnWidth + 2 + 1; // width + padding + border
        var planMaxWidth = consoleWidth.HasValue ? consoleWidth.Value - selectorOverhead : (int?)null;

        var options = new DisplayRenderOptions(
            _display?.Style ?? ObjectRenderStyle.Compact,
            planMaxWidth);

        var plan = _display?.BuildInlineTablePlan(items, options);
        var box = InlineTablePlan.GetBoxCharacters(plan?.BoxStyle ?? tableTheme.BoxStyle);

        int[] columnWidths;
        string[] headers;
        DisplayTableAlignment[] alignments;
        bool[] isIndex;
        string[][] rows;

        if (plan is { HasColumns: true })
        {
            columnWidths = plan.Columns.Select(c => c.Width).ToArray();
            headers = plan.Columns.Select(c => c.Header).ToArray();
            alignments = plan.Columns.Select(c => c.Alignment).ToArray();
            isIndex = plan.Columns.Select(c => c.UseIndexTheme).ToArray();
            rows = plan.Rows.Select(r => r.ToArray()).ToArray();
        }
        else
        {
            // Fallback: index + value columns for items without renderable table columns
            var labels = FormatLabels(items, displayProperty);
            var indexWidth = Math.Max(1, items.Count.ToString().Length);
            var maxLabelWidth = labels.Length > 0 ? labels.Max(l => StripAnsi(l).Length) : 5;

            if (planMaxWidth.HasValue)
            {
                var overhead = indexWidth + 2 + 1 + 2 + 1; // index padding/border + value padding/border
                maxLabelWidth = Math.Min(maxLabelWidth, Math.Max(5, planMaxWidth.Value - overhead));
            }

            columnWidths = [indexWidth, maxLabelWidth];
            headers = ["#", "Value"];
            alignments = [DisplayTableAlignment.Right, DisplayTableAlignment.Left];
            isIndex = [true, false];
            rows = new string[items.Count][];

            for (var i = 0; i < items.Count; i++)
            {
                rows[i] = [i.ToString(), labels[i]];
            }
        }

        // Total width: selector + data columns, each with padding(2), plus borders
        var allWidths = new int[columnWidths.Length + 1];
        allWidths[0] = SelectorColumnWidth;
        Array.Copy(columnWidths, 0, allWidths, 1, columnWidths.Length);
        var totalWidth = allWidths.Sum(w => w + 2) + allWidths.Length + 1;

        return new TableLayout
        {
            Box = box,
            BorderStyle = tableTheme.Border,
            HeaderStyle = tableTheme.Header,
            IndexStyle = tableTheme.Index,
            SelectionStyle = tableTheme.Selection,
            TitleStyle = tuiTheme.Title,
            FooterStyle = tuiTheme.Footer,
            SearchLabelStyle = tuiTheme.SearchLabel,
            SearchInputStyle = tuiTheme.SearchInput,
            DataColumnWidths = columnWidths,
            DataHeaders = headers,
            Alignments = alignments,
            IsIndexColumn = isIndex,
            DataRows = rows,
            TotalTableWidth = totalWidth,
        };
    }

    // ── Table rendering ───────────────────────────────────────

    private static string BuildTopSpanBorder(TableLayout t)
    {
        var innerWidth = t.TotalTableWidth - 2;
        return t.BorderStyle.Apply(
            $"{t.Box.TopLeft}{new string(t.Box.Horizontal, innerWidth)}{t.Box.TopRight}").ToAnsi();
    }

    private static string BuildBottomSpanBorder(TableLayout t)
    {
        var innerWidth = t.TotalTableWidth - 2;
        return t.BorderStyle.Apply(
            $"{t.Box.BottomLeft}{new string(t.Box.Horizontal, innerWidth)}{t.Box.BottomRight}").ToAnsi();
    }

    private static string BuildSpanRow(string text, TableLayout t, ToshTextStyleConfig contentStyle)
    {
        var v = t.BorderStyle.Apply(t.Box.Vertical.ToString()).ToAnsi();
        var innerWidth = t.TotalTableWidth - 2;
        var content = $" {text} ";

        if (content.Length > innerWidth)
        {
            content = content[..Math.Max(1, innerWidth - 1)] + "…";
        }

        var padded = InlineTablePlan.PadRight(content, innerWidth);
        return $"{v}{contentStyle.Apply(padded).ToAnsi()}{v}";
    }

    private static string BuildSearchRow(string label, string searchText, int matchCount, int totalCount, TableLayout t)
    {
        var v = t.BorderStyle.Apply(t.Box.Vertical.ToString()).ToAnsi();
        var innerWidth = t.TotalTableWidth - 2;
        var labelPart = $" {label} ";
        var countPart = $" ({matchCount}/{totalCount})";

        var maxSearch = Math.Max(0, innerWidth - labelPart.Length - countPart.Length);
        if (searchText.Length > maxSearch)
        {
            searchText = searchText[..maxSearch];
        }

        var usedWidth = labelPart.Length + searchText.Length + countPart.Length;
        var trailing = Math.Max(0, innerWidth - usedWidth);

        return $"{v}"
             + t.SearchLabelStyle.Apply(labelPart).ToAnsi()
             + t.SearchInputStyle.Apply(searchText).ToAnsi()
             + t.FooterStyle.Apply(countPart).ToAnsi()
             + $"{new string(' ', trailing)}{v}";
    }

    private static string BuildSpanToColumnsSeparator(TableLayout t)
    {
        var allWidths = GetAllColumnWidths(t);
        var line = InlineTablePlan.BuildBorder(
            allWidths, t.Box.MiddleLeft, t.Box.TopMiddle, t.Box.MiddleRight, t.Box.Horizontal);
        return t.BorderStyle.Apply(line).ToAnsi();
    }

    private static string BuildColumnsToSpanSeparator(TableLayout t)
    {
        var allWidths = GetAllColumnWidths(t);
        var line = InlineTablePlan.BuildBorder(
            allWidths, t.Box.MiddleLeft, t.Box.BottomMiddle, t.Box.MiddleRight, t.Box.Horizontal);
        return t.BorderStyle.Apply(line).ToAnsi();
    }

    private static string BuildHeaderRow(TableLayout t)
    {
        var v = t.BorderStyle.Apply(t.Box.Vertical.ToString()).ToAnsi();
        var sb = new StringBuilder();

        // Selector header "S"
        var selectorHeader = t.HeaderStyle.Apply(InlineTablePlan.PadCenter("S", SelectorColumnWidth)).ToAnsi();
        sb.Append($"{v} {selectorHeader} ");

        // Data column headers
        for (var c = 0; c < t.DataHeaders.Length; c++)
        {
            var header = InlineTablePlan.ClipCell(t.DataHeaders[c], t.DataColumnWidths[c]);
            var padded = InlineTablePlan.PadCenter(header, t.DataColumnWidths[c]);
            sb.Append($"{v} {t.HeaderStyle.Apply(padded).ToAnsi()} ");
        }

        sb.Append(v);
        return sb.ToString();
    }

    private static string BuildColumnSeparator(TableLayout t)
    {
        var allWidths = GetAllColumnWidths(t);
        var line = InlineTablePlan.BuildBorder(
            allWidths, t.Box.MiddleLeft, t.Box.MiddleMiddle, t.Box.MiddleRight, t.Box.Horizontal);
        return t.BorderStyle.Apply(line).ToAnsi();
    }

    private static string BuildDataRow(TableLayout t, int rowIndex, string marker, bool isCursor)
    {
        var v = t.BorderStyle.Apply(t.Box.Vertical.ToString()).ToAnsi();
        var row = rowIndex < t.DataRows.Length ? t.DataRows[rowIndex] : [];
        var sb = new StringBuilder();

        // Selector cell
        var paddedMarker = InlineTablePlan.PadCenter(marker, SelectorColumnWidth);
        sb.Append(isCursor
            ? $"{v} {t.SelectionStyle.Apply(paddedMarker).ToAnsi()} "
            : $"{v} {paddedMarker} ");

        // Data cells
        for (var c = 0; c < t.DataColumnWidths.Length; c++)
        {
            var cell = c < row.Length ? row[c] : string.Empty;
            var clipped = InlineTablePlan.ClipCell(cell, t.DataColumnWidths[c]);

            var padded = c < t.Alignments.Length && t.Alignments[c] == DisplayTableAlignment.Right
                ? InlineTablePlan.PadLeft(clipped, t.DataColumnWidths[c])
                : InlineTablePlan.PadRight(clipped, t.DataColumnWidths[c]);

            if (isCursor)
            {
                // Strip ANSI and apply selection style for uniform highlighting
                padded = t.SelectionStyle.Apply(StyledText.StripAnsi(padded)).ToAnsi();
            }
            else if (c < t.IsIndexColumn.Length && t.IsIndexColumn[c]
                     && !string.IsNullOrEmpty(StyledText.StripAnsi(clipped)))
            {
                padded = t.IndexStyle.Apply(padded).ToAnsi();
            }

            sb.Append($"{v} {padded} ");
        }

        sb.Append(v);
        return sb.ToString();
    }

    private static string BuildEmptyRow(TableLayout t)
    {
        var v = t.BorderStyle.Apply(t.Box.Vertical.ToString()).ToAnsi();
        var sb = new StringBuilder();

        // Empty selector cell
        sb.Append($"{v} {new string(' ', SelectorColumnWidth)} ");

        // Empty data cells
        for (var c = 0; c < t.DataColumnWidths.Length; c++)
        {
            sb.Append($"{v} {new string(' ', t.DataColumnWidths[c])} ");
        }

        sb.Append(v);
        return sb.ToString();
    }

    private static string GetMarker(bool isCursor, bool isSelected, bool multiSelect)
    {
        if (multiSelect)
        {
            if (isSelected) return "✓";
            if (isCursor) return "›";
            return " ";
        }

        return isCursor ? "›" : " ";
    }

    private static int[] GetAllColumnWidths(TableLayout t)
    {
        var all = new int[t.DataColumnWidths.Length + 1];
        all[0] = SelectorColumnWidth;
        Array.Copy(t.DataColumnWidths, 0, all, 1, t.DataColumnWidths.Length);
        return all;
    }

    // ── Formatting helpers ────────────────────────────────────

    private string[] FormatLabels(IReadOnlyList<object?> items, string? displayProperty)
    {
        var labels = new string[items.Count];

        for (var i = 0; i < items.Count; i++)
        {
            labels[i] = FormatItem(items[i], displayProperty);
        }

        return labels;
    }

    private string FormatItem(object? item, string? displayProperty)
    {
        if (item is null) return "(null)";

        var type = item.GetType();
        var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase;

        if (displayProperty is not null)
        {
            var prop = type.GetProperty(displayProperty, flags);

            if (prop is not null)
            {
                return prop.GetValue(item)?.ToString() ?? "(null)";
            }
        }

        if (displayProperty is null && _formatter is not null)
        {
            var options = new ObjectFormattingOptions(ObjectRenderStyle.Compact);

            if (_formatter.TryRenderProfile(item, options, DisplaySurface.Root, out var text) && !string.IsNullOrWhiteSpace(text))
            {
                return StripAnsi(text);
            }
        }

        if (displayProperty is null && !type.IsPrimitive && type != typeof(string) && !type.IsEnum)
        {
            var label = TryGetProperty(item, type, "Name", flags)
                     ?? TryGetProperty(item, type, "DisplayName", flags)
                     ?? TryGetProperty(item, type, "Title", flags)
                     ?? TryGetProperty(item, type, "Label", flags);

            if (label is not null)
            {
                return label;
            }
        }

        return item.ToString() ?? "(null)";
    }

    private static string? TryGetProperty(object item, Type type, string name, System.Reflection.BindingFlags flags)
    {
        var prop = type.GetProperty(name, flags);
        return prop is not null ? prop.GetValue(item)?.ToString() : null;
    }

    private static string StripAnsi(string text)
    {
        return AnsiEscapePattern().Replace(text, string.Empty);
    }

    private static int? TryGetConsoleWidth()
    {
        try
        {
            if (Console.IsOutputRedirected) return null;
            return Console.WindowWidth > 1 ? Console.WindowWidth - 1 : null;
        }
        catch
        {
            return null;
        }
    }

    // ── Terminal helpers ──────────────────────────────────────

    private static void ReserveLines(int count)
    {
        for (var i = 0; i < count; i++)
        {
            Console.WriteLine();
        }
    }

    private static void MoveUp(int lines)
    {
        Console.Write($"\x1b[{lines}A");
    }

    private static void CleanupLines(int totalLines)
    {
        // After rendering, cursor is at end of the last line (no trailing newline),
        // so we only need totalLines-1 to reach the first line.
        MoveUp(totalLines - 1);

        for (var i = 0; i < totalLines; i++)
        {
            Console.Write(ClearLine);
            Console.WriteLine();
        }

        MoveUp(totalLines);
    }

    private static string? ReadPassword()
    {
        var buffer = new StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    return buffer.ToString();
                case ConsoleKey.Escape:
                    Console.Write($" \x1b[2mcancelled\x1b[0m");
                    Console.WriteLine();
                    return null;
                case ConsoleKey.Backspace:
                    if (buffer.Length > 0)
                    {
                        buffer.Length--;
                        Console.Write("\b \b");
                    }

                    break;
                default:
                    if (key.KeyChar >= ' ')
                    {
                        buffer.Append(key.KeyChar);
                        Console.Write('*');
                    }

                    break;
            }
        }
    }
}
