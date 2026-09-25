using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace Tosh.Runtime;

public sealed partial class DisplayEngine
{
    private bool TryGetRenderableColumns(
        IReadOnlyList<object?> values,
        DisplayRenderOptions options,
        out object[] rows,
        out IReadOnlyList<DisplayTableColumn> columns)
    {
        rows = Array.Empty<object>();
        columns = Array.Empty<DisplayTableColumn>();

        if (values.Count == 0 || values.Any(value => value is null))
        {
            return false;
        }

        rows = values.Cast<object>().ToArray();

        if (rows.All(row => row is IShellJobDisplayRow))
        {
            columns =
            [
                new DisplayTableColumn("Kind", row => ((IShellJobDisplayRow)row).Kind, MinWidth: 4, MaxWidth: 12, Priority: 0, CanHide: false),
                new DisplayTableColumn("JobId", row => ((IShellJobDisplayRow)row).JobId, DisplayTableAlignment.Right, MinWidth: 2, MaxWidth: 6, Priority: 10),
                new DisplayTableColumn("Pid", row => ((IShellJobDisplayRow)row).ProcessId, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 8, Priority: 20),
                new DisplayTableColumn("Status", row => ((IShellJobDisplayRow)row).Status, MinWidth: 7, MaxWidth: 10, Priority: 30),
                new DisplayTableColumn("ExitCode", row => ((IShellJobDisplayRow)row).ExitCode, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 8, Priority: 40),
                new DisplayTableColumn("Summary", row => ((IShellJobDisplayRow)row).Summary, MinWidth: 12, MaxWidth: 72, Priority: 50, CanHide: false),
            ];
            return true;
        }

        var rowType = rows[0].GetType();

        if (rows.Any(row => row.GetType() != rowType))
        {
            if (rows.All(row => row is FileSystemInfo))
            {
                rowType = typeof(FileSystemInfo);
            }
            else
            {
                return false;
            }
        }

        var tableContext = new DisplayTableContext(rowType, rows, options);
        var profile = _profiles.Resolve(rowType);

        if (profile is not null)
        {
            if (profile.TryBuildTable(tableContext, out columns))
            {
                columns = ApplyColumnPreferences(rowType, rows.FirstOrDefault(), columns, allowStructuredValues: false, options);
                return columns.Count > 0;
            }

            columns = Array.Empty<DisplayTableColumn>();
            return false;
        }

        if (TryBuildRecordLikeColumns(rows, out columns))
        {
            columns = ApplyColumnPreferences(rowType, rows.FirstOrDefault(), columns, allowStructuredValues: false, options);
            return columns.Count > 0;
        }

        columns = BuildGenericColumns(rowType);
        columns = ApplyColumnPreferences(rowType, rows.FirstOrDefault(), columns, allowStructuredValues: false, options);
        return columns.Count > 0;
    }

    private string RenderTable(
        IReadOnlyList<object> rows,
        IReadOnlyList<DisplayTableColumn> columns,
        DisplayRenderOptions options,
        bool includeIndexColumn = true,
        string? title = null)
    {
        var theme = TableTheme;
        var box = GetBoxCharacters(theme.BoxStyle);
        var isTreeTable = TryCreateTreeTableRows(
            rows,
            columns,
            theme,
            box,
            out var displayRows,
            out var treeColumnIndex,
            out var treePrefixes);
        var effectiveColumns = BuildEffectiveColumns(displayRows, columns, includeIndexColumn);
        var rawCells = displayRows
            .Select((row, index) => BuildRowCells(
                index,
                row,
                columns,
                options,
                includeIndexColumn,
                isTreeTable ? treeColumnIndex : null,
                isTreeTable ? treePrefixes[index] : null))
            .ToArray();

        var visibleColumns = BuildVisibleColumns(effectiveColumns, rawCells, options);

        if (visibleColumns.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        var headerCells = visibleColumns.Select(column => ClipCell(column.Column.Header, column.Width)).ToArray();

        if (string.IsNullOrWhiteSpace(title))
        {
            builder.AppendLine(BuildTableBorder(visibleColumns, box.TopLeft, box.TopMiddle, box.TopRight, box.Horizontal, theme));
        }
        else
        {
            var totalTableWidth = visibleColumns.Sum(c => c.Width + 2) + visibleColumns.Count + 1;
            var borderSpanWidth = Math.Max(1, totalTableWidth - 2);
            var titleWidth = Math.Max(1, totalTableWidth - 4);
            builder.AppendLine(BuildSpanBorder(borderSpanWidth, box.TopLeft, box.TopRight, box.Horizontal, theme));
            builder.AppendLine(BuildSpanningRow(title, titleWidth, box, theme.Header, theme));
            builder.AppendLine(BuildTableBorder(visibleColumns, box.MiddleLeft, box.TopMiddle, box.MiddleRight, box.Horizontal, theme));
        }

        foreach (var line in BuildTableRowLines(
                     headerCells,
                     visibleColumns,
                     box,
                     theme,
                     isHeader: true))
        {
            builder.AppendLine(line);
        }
        builder.AppendLine(BuildTableBorder(visibleColumns, box.MiddleLeft, box.MiddleMiddle, box.MiddleRight, box.Horizontal, theme));

        var totalRenderedLines = GetRenderedRowHeight(headerCells);
        foreach (var row in rawCells)
        {
            var rowCells = visibleColumns.Select(column => row[column.ColumnIndex]).ToArray();
            totalRenderedLines += GetRenderedRowHeight(rowCells);
            foreach (var line in BuildTableRowLines(
                         rowCells,
                         visibleColumns,
                         box,
                         theme))
            {
                builder.AppendLine(line);
            }
        }

        if (ShouldRepeatHeaderAtBottom(totalRenderedLines, rawCells.Length, options))
        {
            builder.AppendLine(BuildTableBorder(visibleColumns, box.MiddleLeft, box.MiddleMiddle, box.MiddleRight, box.Horizontal, theme));
            foreach (var line in BuildTableRowLines(
                         headerCells,
                         visibleColumns,
                         box,
                         theme,
                         isHeader: true))
            {
                builder.AppendLine(line);
            }
        }

        builder.AppendLine(BuildTableBorder(visibleColumns, box.BottomLeft, box.BottomMiddle, box.BottomRight, box.Horizontal, theme));
        return builder.ToString().TrimEnd();
    }

    private bool TryCreateTreeTableRows(
        IReadOnlyList<object> rows,
        IReadOnlyList<DisplayTableColumn> columns,
        ToshTableThemeConfig theme,
        TableBoxCharacters box,
        out object[] flattenedRows,
        out int treeColumnIndex,
        out string[] treePrefixes)
    {
        flattenedRows = rows.ToArray();
        treeColumnIndex = -1;
        treePrefixes = Array.Empty<string>();

        var treeColumn = columns
            .Select((column, index) => new { Column = column, Index = index })
            .FirstOrDefault(item => item.Column.IsTree);

        if (treeColumn is null || rows.Count == 0 || rows.All(row => row is not IDisplayTreeNode))
        {
            return false;
        }

        var flattened = new List<object>();
        var prefixes = new List<string>();

        for (var index = 0; index < rows.Count; index++)
        {
            AddTreeTableRow(
                rows[index],
                flattened,
                prefixes,
                ancestorContinuations: [],
                isLastSibling: index == rows.Count - 1,
                depth: 0,
                theme,
                box);
        }

        if (flattened.Count <= rows.Count)
        {
            return false;
        }

        flattenedRows = flattened.ToArray();
        treeColumnIndex = treeColumn.Index;
        treePrefixes = prefixes.ToArray();
        return true;
    }

    private void AddTreeTableRow(
        object row,
        List<object> flattenedRows,
        List<string> prefixes,
        IReadOnlyList<bool> ancestorContinuations,
        bool isLastSibling,
        int depth,
        ToshTableThemeConfig theme,
        TableBoxCharacters box)
    {
        flattenedRows.Add(row);
        prefixes.Add(BuildTreePrefix(ancestorContinuations, isLastSibling, depth, theme, box));

        if (row is not IDisplayTreeNode treeNode)
        {
            return;
        }

        var children = treeNode
            .GetDisplayChildren()
            .Where(child => child is not null)
            .ToArray();

        if (children.Length == 0)
        {
            return;
        }

        var childAncestorContinuations = depth == 0
            ? ancestorContinuations
            : ancestorContinuations.Concat([!isLastSibling]).ToArray();

        for (var index = 0; index < children.Length; index++)
        {
            AddTreeTableRow(
                children[index],
                flattenedRows,
                prefixes,
                childAncestorContinuations,
                index == children.Length - 1,
                depth + 1,
                theme,
                box);
        }
    }

    private static string BuildTreePrefix(
        IReadOnlyList<bool> ancestorContinuations,
        bool isLastSibling,
        int depth,
        ToshTableThemeConfig theme,
        TableBoxCharacters box)
    {
        if (depth == 0)
        {
            return string.Empty;
        }

        var branch = isLastSibling ? '└' : '├';
        var builder = new StringBuilder();

        foreach (var hasContinuation in ancestorContinuations)
        {
            builder.Append(hasContinuation ? box.Vertical : ' ');
            builder.Append("   ");
        }

        builder.Append(branch);
        builder.Append(box.Horizontal);
        builder.Append(box.Horizontal);
        builder.Append(' ');
        return theme.Border.Apply(builder.ToString()).ToAnsi();
    }

    private IReadOnlyList<DisplayTableColumn> ApplyColumnPreferences(
        Type rowType,
        object? sample,
        IReadOnlyList<DisplayTableColumn> columns,
        bool allowStructuredValues,
        DisplayRenderOptions options)
    {
        columns = ApplyUserTableColumns(rowType, sample, columns);

        if (columns.Count == 0)
        {
            return columns;
        }

        var selection = options.ColumnSelectionResolver?.Invoke(sample);

        if (selection is null || !selection.HasOverrides)
        {
            return columns;
        }

        return ApplyDisplaySelection(rowType, sample, columns, selection, allowStructuredValues, options);
    }

    private IReadOnlyList<DisplayTableColumn> ApplyUserTableColumns(
        Type rowType,
        object? sample,
        IReadOnlyList<DisplayTableColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(rowType);
        ArgumentNullException.ThrowIfNull(columns);

        if (columns.Count == 0 ||
            Preferences?.Profiles is not { } profilePreferences ||
            !profilePreferences.TryResolve(rowType, sample, out var profile) ||
            profile.TableColumns.Count == 0)
        {
            return columns;
        }

        var byHeader = columns.ToDictionary(column => column.Header, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<DisplayTableColumn>(profile.TableColumns.Count);

        foreach (var columnName in profile.TableColumns)
        {
            if (!byHeader.TryGetValue(columnName, out var column))
            {
                continue;
            }

            if (ordered.Any(existing => string.Equals(existing.Header, column.Header, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            ordered.Add(column with
            {
                Priority = ordered.Count * 10,
                CanHide = ordered.Count > 0 && column.CanHide,
            });
        }

        return ordered.Count > 0 ? ordered : columns;
    }

    private IReadOnlyList<DisplayTableColumn> ApplyDisplaySelection(
        Type rowType,
        object? sample,
        IReadOnlyList<DisplayTableColumn> columns,
        DisplayColumnSelection selection,
        bool allowStructuredValues,
        DisplayRenderOptions options)
    {
        var available = BuildSelectableColumns(rowType, sample, columns, allowStructuredValues, options);

        if (selection.ShowColumns.Count > 0)
        {
            var ordered = new List<DisplayTableColumn>(selection.ShowColumns.Count);
            var missing = new List<string>();

            foreach (var name in selection.ShowColumns)
            {
                if (!TryFindColumnBySelectionName(available, name, out var column))
                {
                    missing.Add(name);
                    continue;
                }

                if (ordered.Any(existing => ColumnsShareSelectionName(existing, column)))
                {
                    continue;
                }

                ordered.Add(column with
                {
                    Priority = ordered.Count * 10,
                    CanHide = ordered.Count > 0 && column.CanHide,
                });
            }

            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Unknown column selection for {ObjectFormatter.GetTypeName(rowType)}: {string.Join(", ", missing)}.");
            }

            columns = ordered;
        }
        else if (selection.ShowAll)
        {
            columns = available
                .Select((column, index) => column with
                {
                    Priority = index * 10,
                    CanHide = index > 0 && column.CanHide,
                })
                .ToArray();
        }

        if (selection.HideColumns.Count == 0)
        {
            return columns;
        }

        columns = columns
            .Where(column => !MatchesAnySelectionName(column, selection.HideColumns))
            .ToArray();

        if (columns.Count == 0)
        {
            throw new InvalidOperationException(
                $"Display selection for {ObjectFormatter.GetTypeName(rowType)} removed all visible columns.");
        }

        return columns;
    }

    private IReadOnlyList<DisplayTableColumn> BuildSelectableColumns(
        Type rowType,
        object? sample,
        IReadOnlyList<DisplayTableColumn> columns,
        bool allowStructuredValues,
        DisplayRenderOptions options)
    {
        var merged = new List<DisplayTableColumn>(columns);

        var profile = _profiles.Resolve(rowType);
        var profileContext = new DisplayTableContext(
            rowType,
            sample is null ? Array.Empty<object>() : [sample],
            options);

        if (profile is not null &&
            profile.TryBuildSelectableColumns(profileContext, out var selectable))
        {
            foreach (var column in selectable)
            {
                if (merged.Any(existing => ColumnsShareSelectionName(existing, column)))
                {
                    continue;
                }

                merged.Add(column);
            }
        }

        IReadOnlyList<DisplayTableColumn> additional = sample is not null && ShellRecordUtilities.IsRecordLike(sample)
            ? BuildRecordLikeColumns([sample], allowStructuredValues)
            : BuildGenericColumns(rowType, allowStructuredValues, maxColumns: null);

        foreach (var column in additional)
        {
            if (merged.Any(existing => ColumnsShareSelectionName(existing, column)))
            {
                continue;
            }

            merged.Add(column);
        }

        return merged;
    }

    private static bool TryFindColumnBySelectionName(IReadOnlyList<DisplayTableColumn> columns, string name, out DisplayTableColumn column)
    {
        foreach (var candidate in columns)
        {
            if (MatchesSelectionName(candidate, name))
            {
                column = candidate;
                return true;
            }
        }

        column = null!;
        return false;
    }

    private static bool MatchesAnySelectionName(DisplayTableColumn column, IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            if (MatchesSelectionName(column, name))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesSelectionName(DisplayTableColumn column, string name)
    {
        return string.Equals(column.Header, name, StringComparison.OrdinalIgnoreCase) ||
               (!string.IsNullOrWhiteSpace(column.SelectionKey) &&
                string.Equals(column.SelectionKey, name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ColumnsShareSelectionName(DisplayTableColumn left, DisplayTableColumn right)
    {
        if (string.Equals(left.Header, right.Header, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(left.SelectionKey) &&
            !string.IsNullOrWhiteSpace(right.SelectionKey) &&
            string.Equals(left.SelectionKey, right.SelectionKey, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(left.SelectionKey) &&
            string.Equals(left.SelectionKey, right.Header, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(right.SelectionKey) &&
               string.Equals(right.SelectionKey, left.Header, StringComparison.OrdinalIgnoreCase);
    }

    private string RenderScalarValueTable(
        string typeName,
        string valueText,
        DisplayTableAlignment valueAlignment,
        DisplayRenderOptions options)
    {
        var theme = TableTheme;
        var box = GetBoxCharacters(theme.BoxStyle);
        var typeWidth = GetCellDisplayWidth(typeName);
        var valueLines = SplitLines(valueText);
        var valueWidth = valueLines.Count == 0 ? 0 : valueLines.Max(GetCellDisplayWidth);
        valueWidth = ApplyRecordWidthLimit(typeWidth, valueWidth, options.MaxWidth);

        var builder = new StringBuilder();
        builder.AppendLine(BuildRecordBorder(typeWidth, valueWidth, box.TopLeft, box.TopMiddle, box.TopRight, box.Horizontal, theme));

        for (var index = 0; index < valueLines.Count; index++)
        {
            var name = index == 0 ? typeName : string.Empty;
            var paddedName = PadCellRight(ClipCell(name, typeWidth), typeWidth);
            var styledName = string.IsNullOrEmpty(name)
                ? paddedName
                : theme.Header.Apply(paddedName).ToAnsi();
            var paddedValue = valueAlignment == DisplayTableAlignment.Right
                ? PadCellLeft(ClipCell(valueLines[index], valueWidth), valueWidth)
                : PadCellRight(ClipCell(valueLines[index], valueWidth), valueWidth);
            var vertical = theme.Border.Apply(box.Vertical.ToString()).ToAnsi();
            builder.AppendLine($"{vertical} {styledName} {vertical} {paddedValue} {vertical}");
        }

        builder.AppendLine(BuildRecordBorder(typeWidth, valueWidth, box.BottomLeft, box.BottomMiddle, box.BottomRight, box.Horizontal, theme));
        return builder.ToString().TrimEnd();
    }

    private string RenderTitledTable(
        string title,
        IReadOnlyList<DisplayTableColumn> columns,
        IReadOnlyList<string[]> rawCells,
        DisplayRenderOptions options,
        bool includeHeader)
    {
        var theme = TableTheme;
        var box = GetBoxCharacters(theme.BoxStyle);
        var visibleColumns = BuildVisibleColumns(columns, rawCells, options);

        if (visibleColumns.Count == 0)
        {
            return string.Empty;
        }

        EnsureTitleFits(visibleColumns, title, options);

        var totalWidth = CalculateTotalWidth(visibleColumns);
        var borderSpanWidth = Math.Max(1, totalWidth - 2);
        var titleWidth = Math.Max(1, totalWidth - 4);
        var builder = new StringBuilder();

        builder.AppendLine(BuildSpanBorder(borderSpanWidth, box.TopLeft, box.TopRight, box.Horizontal, theme));
        builder.AppendLine(BuildSpanningRow(title, titleWidth, box, theme.Header, theme));
        builder.AppendLine(BuildTableBorder(visibleColumns, box.MiddleLeft, box.TopMiddle, box.MiddleRight, box.Horizontal, theme));

        if (includeHeader)
        {
            var headerCells = visibleColumns.Select(column => ClipCell(column.Column.Header, column.Width)).ToArray();
            foreach (var line in BuildTableRowLines(
                         headerCells,
                         visibleColumns,
                         box,
                         theme,
                         isHeader: true))
            {
                builder.AppendLine(line);
            }
            builder.AppendLine(BuildTableBorder(visibleColumns, box.MiddleLeft, box.MiddleMiddle, box.MiddleRight, box.Horizontal, theme));
        }

        foreach (var row in rawCells)
        {
            foreach (var line in BuildTableRowLines(
                         visibleColumns.Select(column => row[column.ColumnIndex]).ToArray(),
                         visibleColumns,
                         box,
                         theme))
            {
                builder.AppendLine(line);
            }
        }

        builder.AppendLine(BuildTableBorder(visibleColumns, box.BottomLeft, box.BottomMiddle, box.BottomRight, box.Horizontal, theme));
        return builder.ToString().TrimEnd();
    }

    private static void EnsureTitleFits(
        IReadOnlyList<VisibleTableColumn> visibleColumns,
        string title,
        DisplayRenderOptions options)
    {
        if (visibleColumns.Count == 0)
        {
            return;
        }

        var currentTitleWidth = Math.Max(1, CalculateTotalWidth(visibleColumns) - 4);
        var requiredWidth = GetCellDisplayWidth(title);

        if (requiredWidth <= currentTitleWidth)
        {
            return;
        }

        var growth = requiredWidth - currentTitleWidth;

        if (options.MaxWidth is int maxWidth && maxWidth > 0)
        {
            var availableGrowth = Math.Max(0, maxWidth - CalculateTotalWidth(visibleColumns));
            growth = Math.Min(growth, availableGrowth);
        }

        if (growth <= 0)
        {
            return;
        }

        visibleColumns[^1].Width += growth;
    }

    private IReadOnlyList<DisplayTableColumn> BuildEffectiveColumns(
        IReadOnlyList<object> rows,
        IReadOnlyList<DisplayTableColumn> columns,
        bool includeIndexColumn)
    {
        if (!includeIndexColumn)
        {
            return columns.ToArray();
        }

        var indexWidth = Math.Max(1, (rows.Count - 1).ToString().Length);
        var effectiveColumns = new List<DisplayTableColumn>(columns.Count + 1)
        {
            new("#", _ => null, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: indexWidth, Priority: -1, CanHide: false, SelectionKey: "Index"),
        };

        effectiveColumns.AddRange(columns);
        return effectiveColumns;
    }

    private string[] BuildRowCells(
        int rowIndex,
        object row,
        IReadOnlyList<DisplayTableColumn> columns,
        DisplayRenderOptions options,
        bool includeIndexColumn,
        int? treeColumnIndex = null,
        string? treePrefix = null)
    {
        var cells = new string[columns.Count + (includeIndexColumn ? 1 : 0)];
        var cellOffset = includeIndexColumn ? 1 : 0;

        if (includeIndexColumn)
        {
            cells[0] = rowIndex.ToString();
        }

        for (var index = 0; index < columns.Count; index++)
        {
            var cell = FormatTableCellValue(columns[index].ValueAccessor(row), options);

            if (treeColumnIndex == index && !string.IsNullOrEmpty(treePrefix))
            {
                cell = ApplyTreePrefix(cell, treePrefix);
            }

            cells[index + cellOffset] = cell;
        }

        return cells;
    }

    private static string ApplyTreePrefix(string value, string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            return value;
        }

        var lines = SplitLines(value).ToArray();
        var continuationPrefix = new string(' ', StyledText.GetVisibleLength(prefix));

        for (var index = 0; index < lines.Length; index++)
        {
            lines[index] = index == 0
                ? $"{prefix}{lines[index]}"
                : $"{continuationPrefix}{lines[index]}";
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static IReadOnlyList<string> BuildTableRowLines(
        IReadOnlyList<string> cells,
        IReadOnlyList<VisibleTableColumn> columns,
        TableBoxCharacters box,
        ToshTableThemeConfig theme,
        bool isHeader = false)
    {
        var splitCells = cells
            .Select((cell, idx) => WrapCellLines(cell, columns[idx].Width, isHeader))
            .ToArray();
        var rowHeight = splitCells.Max(static lines => lines.Count);
        var lines = new string[rowHeight];
        var vertical = theme.Border.Apply(box.Vertical.ToString()).ToAnsi();

        for (var lineIndex = 0; lineIndex < rowHeight; lineIndex++)
        {
            var renderedCells = cells
                .Select((_, index) =>
                {
                    var line = lineIndex < splitCells[index].Count ? splitCells[index][lineIndex] : string.Empty;
                    var padded = PadCell(ClipCell(line, columns[index].Width), columns[index], isHeader);

                    if (isHeader)
                    {
                        return columns[index].Column.UseHeaderTheme
                            ? theme.Header.Apply(padded).ToAnsi()
                            : padded;
                    }

                    if (index == 0)
                    {
                        return string.IsNullOrEmpty(StyledText.StripAnsi(line))
                            ? padded
                            : columns[index].Column.UseIndexTheme
                                ? theme.Index.Apply(padded).ToAnsi()
                                : padded;
                    }

                    return padded;
                })
                .ToArray();

            lines[lineIndex] = $"{vertical} {string.Join($" {vertical} ", renderedCells)} {vertical}";
        }

        return lines;
    }

    private static string BuildTableBorder(
        IReadOnlyList<VisibleTableColumn> columns,
        char left,
        char center,
        char right,
        char horizontal,
        ToshTableThemeConfig theme)
    {
        var line = $"{left}{string.Join(center, columns.Select(column => new string(horizontal, column.Width + 2)))}{right}";
        return theme.Border.Apply(line).ToAnsi();
    }

    private static string BuildSpanBorder(
        int width,
        char left,
        char right,
        char horizontal,
        ToshTableThemeConfig theme)
    {
        return theme.Border.Apply($"{left}{new string(horizontal, width)}{right}").ToAnsi();
    }

    private static string BuildRecordBorder(
        int nameWidth,
        int valueWidth,
        char left,
        char center,
        char right,
        char horizontal,
        ToshTableThemeConfig theme)
    {
        return theme.Border.Apply($"{left}{new string(horizontal, nameWidth + 2)}{center}{new string(horizontal, valueWidth + 2)}{right}").ToAnsi();
    }

    private static string BuildSpanningRow(
        string value,
        int width,
        TableBoxCharacters box,
        ToshTextStyleConfig style,
        ToshTableThemeConfig theme)
    {
        var vertical = theme.Border.Apply(box.Vertical.ToString()).ToAnsi();
        var padded = PadCellRight(ClipCell(value, width), width);
        return $"{vertical} {style.Apply(padded).ToAnsi()} {vertical}";
    }

    private List<VisibleTableColumn> BuildVisibleColumns(
        IReadOnlyList<DisplayTableColumn> columns,
        IReadOnlyList<string[]> rawCells,
        DisplayRenderOptions options)
    {
        var desiredWidths = GetDesiredWidths(columns, rawCells);
        var visibleColumns = columns
            .Select((column, index) => new VisibleTableColumn(
                index,
                column,
                desiredWidths[index]))
            .ToList();

        if (options.MaxWidth is not int maxWidth || maxWidth <= 0)
        {
            return visibleColumns;
        }

        while (visibleColumns.Count > 1 && CalculateTotalWidth(visibleColumns) > maxWidth)
        {
            var toRemove = visibleColumns
                .Where(column => column.Column.CanHide)
                .OrderByDescending(column => column.Column.Priority)
                .ThenByDescending(column => column.ColumnIndex)
                .FirstOrDefault();

            if (toRemove is null)
            {
                break;
            }

            visibleColumns.Remove(toRemove);
        }

        while (CalculateTotalWidth(visibleColumns) > maxWidth)
        {
            var shrinkable = visibleColumns
                .Where(column => column.Width > column.MinWidth)
                .OrderByDescending(column => column.Width - column.MinWidth)
                .ThenByDescending(column => column.Column.Priority)
                .ThenByDescending(column => column.ColumnIndex)
                .ToList();

            if (shrinkable.Count == 0)
            {
                break;
            }

            var excess = CalculateTotalWidth(visibleColumns) - maxWidth;

            foreach (var candidate in shrinkable)
            {
                if (excess <= 0)
                {
                    break;
                }

                var available = candidate.Width - candidate.MinWidth;
                var reduction = Math.Min(available, excess);
                candidate.Width -= reduction;
                excess -= reduction;
            }
        }

        if (visibleColumns.Count == 1 && visibleColumns[0].Width > maxWidth)
        {
            visibleColumns[0].Width = Math.Max(1, maxWidth - 4);
        }

        return visibleColumns;
    }

    private static int[] GetDesiredWidths(
        IReadOnlyList<DisplayTableColumn> columns,
        IReadOnlyList<string[]> rawCells)
    {
        var maxWidths = new int[columns.Count];

        foreach (var row in rawCells)
        {
            for (var i = 0; i < columns.Count && i < row.Length; i++)
            {
                var cellWidth = GetCellDisplayWidth(row[i]);
                if (cellWidth > maxWidths[i])
                {
                    maxWidths[i] = cellWidth;
                }
            }
        }

        for (var i = 0; i < columns.Count; i++)
        {
            var desiredWidth = Math.Max(columns[i].Header.Length, maxWidths[i]);
            maxWidths[i] = Math.Max(1, Math.Min(desiredWidth, columns[i].MaxWidth));
        }

        return maxWidths;
    }

    private static int CalculateTotalWidth(IReadOnlyList<VisibleTableColumn> columns)
    {
        if (columns.Count == 0)
        {
            return 0;
        }

        return columns.Sum(column => column.Width + 2) + columns.Count + 1;
    }

    private static string PadCell(string cell, VisibleTableColumn column, bool isHeader)
    {
        if (isHeader)
        {
            var extra = column.Width - StyledText.GetVisibleLength(cell);
            var leftPadding = extra / 2;
            var rightPadding = extra - leftPadding;
            return $"{new string(' ', leftPadding)}{cell}{new string(' ', rightPadding)}";
        }

        return column.Column.Alignment == DisplayTableAlignment.Right
            ? PadCellLeft(cell, column.Width)
            : PadCellRight(cell, column.Width);
    }

    private static string ClipCell(string value, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        if (StyledText.GetVisibleLength(value) <= width)
        {
            return value;
        }

        // `TUI-0005`, the same cut as `InlineTablePlan.ClipCell`: a column budget used as a
        // character index. `Elide` counts columns and never splits a cluster.
        return TextMeasure.Elide(StyledText.StripAnsi(value), width);
    }

    private static bool ShouldRepeatHeaderAtBottom(
        int totalRenderedLines,
        int rowCount,
        DisplayRenderOptions options)
    {
        if (options.MaxHeight is not int maxHeight || maxHeight <= 0)
        {
            return false;
        }

        // totalRenderedLines includes header + data row heights.
        // Add borders: top + header separator + bottom + per-row separators estimate.
        var renderedLineCount = totalRenderedLines + 3;
        return renderedLineCount > maxHeight;
    }

    /// <summary>
    /// One table cell as the shell prints it.
    /// </summary>
    /// <remarks>
    /// Public because the TUI's table needs the same text, for the same reason it needs the
    /// same columns: a reader has learned that a file's size reads "133 kB" and its date
    /// reads "62 minutes ago", and a second renderer showing "132736 B" and a timestamp is
    /// a second opinion nobody asked for (<c>TUI-0017</c>).
    /// </remarks>
    public string FormatTableCellValue(object? value, DisplayRenderOptions options)
    {
        return ApplyValueStyling(FormatTableCellValueCore(value, options));
    }

    // Light-touch ANSI styling for "status glyph" cells. Applies only when the
    // visible payload is exactly one of the well-known glyphs, so prose
    // containing "✓ done" is left untouched.
    private string FormatTableCellValueCore(object? value, DisplayRenderOptions options)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (_formatter.TryRenderProfile(
            value,
            new ObjectFormattingOptions(options.Style),
            DisplaySurface.TableCell,
            out var text))
        {
            return text;
        }

        if (value is FileSystemInfo fileSystemInfo)
        {
            return fileSystemInfo.Name;
        }

        if (ObjectFormatter.TryFormatSimple(value, isRoot: true, out var simpleText))
        {
            return simpleText;
        }

        // `TOAST-0021`. A value the *language* renders as a scalar goes in the cell as that
        // scalar. Display's structural view is for things with parts; an enum member has
        // readable properties but is not one of them.
        if (ToastRenderer.RendersAsScalar(value))
        {
            return ToastRenderer.Render(value);
        }

        if (TryFormatNestedStructuredTableCellValue(value, options, out var nestedStructuredText))
        {
            return nestedStructuredText;
        }

        if (ShellRecordUtilities.IsRecordLike(value))
        {
            return _formatter.Format(
                value,
                new ObjectFormattingOptions(
                    options.Style,
                    MaxDepth: 2,
                    MaxCollectionItemCount: 3,
                    MaxPropertyCount: 4));
        }

        if (value is IEnumerable enumerable &&
            value is not string &&
            value is not ShellTextLine &&
            value is not IDictionary)
        {
            return _formatter.Format(
                enumerable,
                new ObjectFormattingOptions(
                    options.Style,
                    MaxDepth: 2,
                    MaxCollectionItemCount: 4,
                    MaxPropertyCount: 4));
        }

        if (value is Type type)
        {
            return type.FullName ?? type.Name;
        }

        return $"<{ObjectFormatter.GetTypeName(value.GetType())}>";
    }

    private bool TryFormatNestedStructuredTableCellValue(
        object value,
        DisplayRenderOptions options,
        out string rendered)
    {
        rendered = string.Empty;

        if (!CanRenderNestedStructuredValue(value) ||
            options.MaxWidth is not int maxWidth ||
            maxWidth <= 0)
        {
            return false;
        }

        var nestedOptions = CreateNestedStructuredRenderOptions(options);
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var trackReference = !value.GetType().IsValueType;

        if (trackReference)
        {
            visited.Add(value);
        }

        rendered = RenderMany([value], nestedOptions, depth: 1, visited);

        if (!rendered.Contains('\n') ||
            ShouldInlineNestedStructuredValue(rendered, nestedOptions))
        {
            rendered = string.Empty;
            return false;
        }

        return true;
    }

    private IReadOnlyList<DisplayTableColumn> BuildGenericColumns(Type rowType, bool allowStructuredValues = false, int? maxColumns = 8)
    {
        if (!CanRenderAsGenericTable(rowType))
        {
            return Array.Empty<DisplayTableColumn>();
        }

        return rowType
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
            .Select(property =>
            {
                var propertyType = property.PropertyType;
                Func<object, object?> valueAccessor = row => ObjectFormatter.SafeGetValue(property, row);

                if (ObjectMemberAdapter.TryGetMember(rowType, property.Name, out var adaptedMember))
                {
                    propertyType = adaptedMember.ValueType;
                    valueAccessor = row => ObjectMemberAdapter.SafeGetValue(row, property.Name);
                }

                return new
                {
                    Property = property,
                    PropertyType = propertyType,
                    ValueAccessor = valueAccessor,
                    Header = GetColumnHeader(property.Name),
                    Order = GetPreferredColumnOrder(property.Name),
                };
            })
            .Where(item => allowStructuredValues || IsRenderableTableCellType(item.PropertyType))
            .OrderBy(item => item.Order)
            .ThenBy(item => item.Property.Name, StringComparer.Ordinal)
            .GroupBy(item => item.Header, StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(maxColumns ?? int.MaxValue)
            .Select((item, index) => new DisplayTableColumn(
                item.Header,
                item.ValueAccessor,
                Alignment: IsRightAlignedType(item.PropertyType) ? DisplayTableAlignment.Right : DisplayTableAlignment.Left,
                Priority: item.Order + index,
                CanHide: index > 0,
                SelectionKey: item.Property.Name))
            .ToArray();
    }

    private static bool CanRenderAsGenericTable(Type rowType)
    {
        if (rowType == typeof(ObjectInspection) ||
            rowType == typeof(FormatterStatus) ||
            rowType == typeof(Type) ||
            typeof(Exception).IsAssignableFrom(rowType))
        {
            return false;
        }

        if (IsIntrinsicRenderableTableValueType(rowType))
        {
            return false;
        }

        if (typeof(IEnumerable).IsAssignableFrom(rowType) && rowType != typeof(FileSystemEntry))
        {
            return false;
        }

        return true;
    }

    private bool IsRenderableTableCellType(Type type)
    {
        var effectiveType = Nullable.GetUnderlyingType(type) ?? type;
        return IsIntrinsicRenderableTableValueType(effectiveType) || _profiles.Resolve(effectiveType) is not null;
    }

    private static bool IsIntrinsicRenderableTableValueType(Type type)
    {
        var effectiveType = Nullable.GetUnderlyingType(type) ?? type;

        return effectiveType == typeof(string) ||
               effectiveType == typeof(char) ||
               effectiveType == typeof(bool) ||
               effectiveType == typeof(DateTime) ||
               effectiveType == typeof(DateTimeOffset) ||
               effectiveType == typeof(StorageSize) ||
               effectiveType == typeof(decimal) ||
               effectiveType == typeof(Guid) ||
               effectiveType == typeof(TimeSpan) ||
               effectiveType == typeof(Uri) ||
               typeof(Units.Quantity).IsAssignableFrom(effectiveType) ||
               effectiveType.IsEnum ||
               effectiveType.IsPrimitive;
    }

    private static bool ShouldPreferTableForSingleItem(Type type)
    {
        var effectiveType = Nullable.GetUnderlyingType(type) ?? type;

        return effectiveType == typeof(Color);
    }

    private static int GetPreferredColumnOrder(string propertyName)
    {
        return propertyName switch
        {
            "Index" => 0,
            "Name" => 1,
            "Kind" => 2,
            "Type" => 2,
            "TypeName" => 2,
            "Description" => 3,
            "Text" => 3,
            "Size" => 4,
            "Length" => 4,
            "Modified" => 5,
            "Timestamp" => 5,
            "When" => 5,
            "LastWriteTime" => 5,
            "Mode" => 6,
            "Usage" => 7,
            _ => 20,
        };
    }

    private static string GetColumnHeader(string propertyName)
    {
        return propertyName switch
        {
            "TypeName" => "Type",
            "Length" => "Size",
            "LastWriteTime" => "Modified",
            "Timestamp" => "When",
            _ => propertyName,
        };
    }

    private static TableBoxCharacters GetBoxCharacters(ToshTableBoxStyle style)
    {
        style = TerminalGlyphs.ResolveBoxStyle(style);

        return style switch
        {
            ToshTableBoxStyle.Square => new('┌', '┬', '┐', '├', '┼', '┤', '└', '┴', '┘', '│', '─'),
            ToshTableBoxStyle.Heavy => new('┏', '┳', '┓', '┣', '╋', '┫', '┗', '┻', '┛', '┃', '━'),
            ToshTableBoxStyle.Ascii => new('+', '+', '+', '+', '+', '+', '+', '+', '+', '|', '-'),
            ToshTableBoxStyle.Double => new('╔', '╦', '╗', '╠', '╬', '╣', '╚', '╩', '╝', '║', '═'),
            _ => new('╭', '┬', '╮', '├', '┼', '┤', '╰', '┴', '╯', '│', '─'),
        };
    }

    private sealed class VisibleTableColumn
    {
        public VisibleTableColumn(int columnIndex, DisplayTableColumn column, int width)
        {
            ColumnIndex = columnIndex;
            Column = column;
            Width = width;
            MinWidth = Math.Min(column.MinWidth, width);
        }

        public int ColumnIndex { get; }

        public DisplayTableColumn Column { get; }

        public int Width { get; set; }

        public int MinWidth { get; }
    }

    private readonly record struct TableBoxCharacters(
        char TopLeft,
        char TopMiddle,
        char TopRight,
        char MiddleLeft,
        char MiddleMiddle,
        char MiddleRight,
        char BottomLeft,
        char BottomMiddle,
        char BottomRight,
        char Vertical,
        char Horizontal);


}
