using System.Collections;
using System.Reflection;
using System.Text;

namespace Tosh.Core;

public sealed class DisplayEngine
{
    private readonly ObjectFormatter _formatter;
    private readonly DisplayProfileRegistry _profiles;

    public DisplayEngine(ObjectFormatter formatter)
    {
        _formatter = formatter;
        _profiles = formatter.Profiles;
    }

    public ObjectRenderStyle Style
    {
        get => _formatter.Style;
        set => _formatter.Style = value;
    }

    public string Render(object? value)
    {
        return Render(value, new DisplayRenderOptions(Style));
    }

    public string Render(object? value, DisplayRenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return _formatter.Format(value, new ObjectFormattingOptions(options.Style));
    }

    public string RenderMany(IReadOnlyList<object?> values)
    {
        return RenderMany(values, new DisplayRenderOptions(Style));
    }

    public string RenderMany(IReadOnlyList<object?> values, DisplayRenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(options);

        if (values.Count == 0)
        {
            return string.Empty;
        }

        if (TryRenderPlainTextLines(values, out var plainText))
        {
            return plainText;
        }

        if (TryRenderSingleEnumerable(values, options, out var enumerable))
        {
            return enumerable;
        }

        if (TryRenderRecord(values, options, out var record))
        {
            return record;
        }

        if (TryRenderTable(values, options, out var table))
        {
            return table;
        }

        if (TryRenderValueList(values, options, out var list))
        {
            return list;
        }

        var rendered = values.Select(value => Render(value, options)).ToList();
        var separator = rendered.Any(item => item.Contains('\n'))
            ? $"{Environment.NewLine}{Environment.NewLine}"
            : Environment.NewLine;

        return string.Join(separator, rendered);
    }

    private static bool TryRenderPlainTextLines(IReadOnlyList<object?> values, out string rendered)
    {
        rendered = string.Empty;

        if (values.Count == 0 || values.Any(value => value is not ShellTextLine))
        {
            return false;
        }

        rendered = string.Join(
            Environment.NewLine,
            values.Cast<ShellTextLine>().Select(line => line.Text));
        return true;
    }

    private bool TryRenderSingleEnumerable(IReadOnlyList<object?> values, DisplayRenderOptions options, out string rendered)
    {
        rendered = string.Empty;

        if (values.Count != 1 ||
            values[0] is null ||
            values[0] is string ||
            values[0] is IDictionary ||
            values[0] is not IEnumerable enumerable)
        {
            return false;
        }

        var items = new List<object?>();

        foreach (var item in enumerable)
        {
            if (ReferenceEquals(item, values[0]))
            {
                return false;
            }

            items.Add(item);
        }

        if (items.Count == 0)
        {
            rendered = "[]";
            return true;
        }

        rendered = RenderMany(items, options);
        return true;
    }

    private bool TryRenderRecord(IReadOnlyList<object?> values, DisplayRenderOptions options, out string table)
    {
        table = string.Empty;

        if (values.Count != 1 || values[0] is null)
        {
            return false;
        }

        if (!TryGetRenderableColumns(values, options, out var rows, out var columns))
        {
            return false;
        }

        table = RenderRecord(rows[0], columns, options);
        return true;
    }

    private bool TryRenderTable(IReadOnlyList<object?> values, DisplayRenderOptions options, out string table)
    {
        table = string.Empty;

        if (values.Count <= 1)
        {
            return false;
        }

        if (!TryGetRenderableColumns(values, options, out var rows, out var columns))
        {
            return false;
        }

        table = RenderTable(rows, columns, options);
        return true;
    }

    private bool TryRenderValueList(IReadOnlyList<object?> values, DisplayRenderOptions options, out string table)
    {
        table = string.Empty;

        if (values.Count <= 1)
        {
            return false;
        }

        var cells = new List<string>(values.Count);

        foreach (var value in values)
        {
            var cell = value is null ? string.Empty : FormatTableCellValue(value, options);

            if (cell.Contains('\n'))
            {
                return false;
            }

            cells.Add(cell);
        }

        table = RenderValueList(cells, options);
        return true;
    }

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
        var rowType = rows[0].GetType();

        if (rows.Any(row => row.GetType() != rowType))
        {
            return false;
        }

        var tableContext = new DisplayTableContext(rowType, rows, options);
        var profile = _profiles.Resolve(rowType);

        if (profile is not null && profile.TryBuildTable(tableContext, out columns))
        {
            return columns.Count > 0;
        }

        columns = BuildGenericColumns(rowType);
        return columns.Count > 0;
    }

    private string RenderTable(IReadOnlyList<object> rows, IReadOnlyList<DisplayTableColumn> columns, DisplayRenderOptions options)
    {
        var effectiveColumns = BuildEffectiveColumns(rows, columns);
        var rawCells = rows
            .Select((row, index) => BuildRowCells(index, row, columns, options))
            .ToArray();

        var visibleColumns = BuildVisibleColumns(effectiveColumns, rawCells, options);

        if (visibleColumns.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine(BuildTableBorder(visibleColumns, '╭', '┬', '╮'));
        builder.AppendLine(BuildTableRow(
            visibleColumns.Select(column => ClipCell(column.Column.Header, column.Width)).ToArray(),
            visibleColumns,
            isHeader: true));
        builder.AppendLine(BuildTableBorder(visibleColumns, '├', '┼', '┤'));

        foreach (var row in rawCells)
        {
            builder.AppendLine(BuildTableRow(
                visibleColumns.Select(column => ClipCell(row[column.ColumnIndex], column.Width)).ToArray(),
                visibleColumns));
        }

        builder.AppendLine(BuildTableBorder(visibleColumns, '╰', '┴', '╯'));
        return builder.ToString().TrimEnd();
    }

    private string RenderRecord(object row, IReadOnlyList<DisplayTableColumn> columns, DisplayRenderOptions options)
    {
        var rows = columns
            .Select(column => new RecordRow(
                column.Header,
                FormatTableCellValue(column.ValueAccessor(row), options),
                column.Alignment))
            .ToArray();

        if (rows.Length == 0)
        {
            return string.Empty;
        }

        var nameWidth = rows.Max(item => item.Name.Length);
        var valueWidth = rows.Max(item => item.Value.Length);
        valueWidth = ApplyRecordWidthLimit(nameWidth, valueWidth, options.MaxWidth);

        var builder = new StringBuilder();
        builder.AppendLine(BuildRecordBorder(nameWidth, valueWidth, '╭', '┬', '╮'));

        foreach (var recordRow in rows)
        {
            builder.AppendLine(BuildRecordRow(recordRow, nameWidth, valueWidth));
        }

        builder.AppendLine(BuildRecordBorder(nameWidth, valueWidth, '╰', '┴', '╯'));
        return builder.ToString().TrimEnd();
    }

    private string RenderValueList(IReadOnlyList<string> values, DisplayRenderOptions options)
    {
        var indexWidth = Math.Max(1, (values.Count - 1).ToString().Length);
        var valueWidth = values.Count == 0 ? 0 : values.Max(item => item.Length);
        valueWidth = ApplyListWidthLimit(indexWidth, valueWidth, options.MaxWidth);

        var builder = new StringBuilder();
        builder.AppendLine(BuildRecordBorder(indexWidth, valueWidth, '╭', '┬', '╮'));

        for (var index = 0; index < values.Count; index++)
        {
            builder.AppendLine(BuildListRow(index.ToString(), values[index], indexWidth, valueWidth));
        }

        builder.AppendLine(BuildRecordBorder(indexWidth, valueWidth, '╰', '┴', '╯'));
        return builder.ToString().TrimEnd();
    }

    private IReadOnlyList<DisplayTableColumn> BuildEffectiveColumns(IReadOnlyList<object> rows, IReadOnlyList<DisplayTableColumn> columns)
    {
        var indexWidth = Math.Max(1, (rows.Count - 1).ToString().Length);
        var effectiveColumns = new List<DisplayTableColumn>(columns.Count + 1)
        {
            new("#", _ => null, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: indexWidth, Priority: -1, CanHide: false),
        };

        effectiveColumns.AddRange(columns);
        return effectiveColumns;
    }

    private string[] BuildRowCells(int rowIndex, object row, IReadOnlyList<DisplayTableColumn> columns, DisplayRenderOptions options)
    {
        var cells = new string[columns.Count + 1];
        cells[0] = rowIndex.ToString();

        for (var index = 0; index < columns.Count; index++)
        {
            cells[index + 1] = FormatTableCellValue(columns[index].ValueAccessor(row), options);
        }

        return cells;
    }

    private static string BuildTableRow(
        IReadOnlyList<string> cells,
        IReadOnlyList<VisibleTableColumn> columns,
        bool isHeader = false)
    {
        var renderedCells = cells
            .Select((cell, index) => PadCell(cell, columns[index], isHeader))
            .ToArray();

        return $"│ {string.Join(" │ ", renderedCells)} │";
    }

    private static string BuildTableBorder(IReadOnlyList<VisibleTableColumn> columns, char left, char center, char right)
    {
        return $"{left}{string.Join(center, columns.Select(column => new string('─', column.Width + 2)))}{right}";
    }

    private static string BuildRecordBorder(int nameWidth, int valueWidth, char left, char center, char right)
    {
        return $"{left}{new string('─', nameWidth + 2)}{center}{new string('─', valueWidth + 2)}{right}";
    }

    private List<VisibleTableColumn> BuildVisibleColumns(
        IReadOnlyList<DisplayTableColumn> columns,
        IReadOnlyList<string[]> rawCells,
        DisplayRenderOptions options)
    {
        var visibleColumns = columns
            .Select((column, index) => new VisibleTableColumn(
                index,
                column,
                GetDesiredWidth(column, index, rawCells, options.MaxTableCellWidth)))
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
            var shrinkCandidate = visibleColumns
                .Where(column => column.Width > column.MinWidth)
                .OrderByDescending(column => column.Width - column.MinWidth)
                .ThenByDescending(column => column.Column.Priority)
                .ThenByDescending(column => column.ColumnIndex)
                .FirstOrDefault();

            if (shrinkCandidate is null)
            {
                break;
            }

            shrinkCandidate.Width--;
        }

        if (visibleColumns.Count == 1 && visibleColumns[0].Width > maxWidth)
        {
            visibleColumns[0].Width = Math.Max(1, maxWidth - 4);
        }

        return visibleColumns;
    }

    private static int GetDesiredWidth(
        DisplayTableColumn column,
        int columnIndex,
        IReadOnlyList<string[]> rawCells,
        int maxTableCellWidth)
    {
        var contentWidth = rawCells.Count == 0 ? 0 : rawCells.Max(row => row[columnIndex].Length);
        var desiredWidth = Math.Max(column.Header.Length, contentWidth);
        var effectiveMaxWidth = Math.Min(column.MaxWidth, maxTableCellWidth);
        return Math.Max(1, Math.Min(desiredWidth, effectiveMaxWidth));
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
            var extra = column.Width - cell.Length;
            var leftPadding = extra / 2;
            var rightPadding = extra - leftPadding;
            return $"{new string(' ', leftPadding)}{cell}{new string(' ', rightPadding)}";
        }

        return column.Column.Alignment == DisplayTableAlignment.Right
            ? cell.PadLeft(column.Width)
            : cell.PadRight(column.Width);
    }

    private static string ClipCell(string value, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        if (value.Length <= width)
        {
            return value;
        }

        return width == 1 ? value[..1] : $"{value[..(width - 1)]}…";
    }

    private static string BuildRecordRow(RecordRow row, int nameWidth, int valueWidth)
    {
        return $"│ {ClipCell(row.Name, nameWidth).PadRight(nameWidth)} │ {PadRecordValue(row.Value, row.Alignment, valueWidth)} │";
    }

    private static string BuildListRow(string index, string value, int indexWidth, int valueWidth)
    {
        return $"│ {ClipCell(index, indexWidth).PadLeft(indexWidth)} │ {ClipCell(value, valueWidth).PadRight(valueWidth)} │";
    }

    private static string PadRecordValue(string value, DisplayTableAlignment alignment, int width)
    {
        var clipped = ClipCell(value, width);
        return alignment == DisplayTableAlignment.Right
            ? clipped.PadLeft(width)
            : clipped.PadRight(width);
    }

    private static int ApplyRecordWidthLimit(int nameWidth, int valueWidth, int? maxWidth)
    {
        if (maxWidth is not int widthLimit || widthLimit <= 0)
        {
            return valueWidth;
        }

        var availableValueWidth = Math.Max(1, widthLimit - nameWidth - 7);
        return Math.Min(valueWidth, availableValueWidth);
    }

    private static int ApplyListWidthLimit(int indexWidth, int valueWidth, int? maxWidth)
    {
        if (maxWidth is not int widthLimit || widthLimit <= 0)
        {
            return valueWidth;
        }

        var availableValueWidth = Math.Max(1, widthLimit - indexWidth - 7);
        return Math.Min(valueWidth, availableValueWidth);
    }

    private string FormatTableCellValue(object? value, DisplayRenderOptions options)
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

        if (value is Type type)
        {
            return type.FullName ?? type.Name;
        }

        return $"<{ObjectFormatter.GetTypeName(value.GetType())}>";
    }

    private IReadOnlyList<DisplayTableColumn> BuildGenericColumns(Type rowType)
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
            .Where(item => IsRenderableTableCellType(item.PropertyType))
            .OrderBy(item => item.Order)
            .ThenBy(item => item.Property.Name, StringComparer.Ordinal)
            .GroupBy(item => item.Header, StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(8)
            .Select((item, index) => new DisplayTableColumn(
                item.Header,
                item.ValueAccessor,
                Alignment: IsRightAlignedType(item.PropertyType) ? DisplayTableAlignment.Right : DisplayTableAlignment.Left,
                Priority: item.Order + index,
                CanHide: index > 0))
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
               effectiveType.IsEnum ||
               effectiveType.IsPrimitive;
    }

    private static bool IsRightAlignedType(Type type)
    {
        var effectiveType = Nullable.GetUnderlyingType(type) ?? type;

        return effectiveType == typeof(byte) ||
               effectiveType == typeof(short) ||
               effectiveType == typeof(int) ||
               effectiveType == typeof(long) ||
               effectiveType == typeof(StorageSize) ||
               effectiveType == typeof(float) ||
               effectiveType == typeof(double) ||
               effectiveType == typeof(decimal);
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

    private sealed record RecordRow(string Name, string Value, DisplayTableAlignment Alignment);
}
