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
    private IReadOnlyList<DisplayTableColumn> BuildRecordLikeColumns(IReadOnlyList<object> rows, bool allowStructuredValues = false)
    {
        if (rows.Count == 0 || !ShellRecordUtilities.TryGetVisibleFields(rows[0], out var fields))
        {
            return Array.Empty<DisplayTableColumn>();
        }

        // Auto-drop columns where every row's value is null or an empty string —
        // typical for sparse cross-source records (e.g. AUR-only columns in a
        // mixed listing). Skip the drop for single-row records so users still
        // see absent fields in the detail view.
        var hidden = rows.Count > 1
            ? new HashSet<string>(
                fields
                    .Select(f => f.Key)
                    .Where(name => rows.All(r =>
                    {
                        if (!ShellRecordUtilities.TryGetValue(r, name, out var v) || v is null) return true;
                        if (v is string s) return s.Length == 0;
                        return false;
                    })),
                StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        return fields
            .Where(field => !hidden.Contains(field.Key))
            .Select((field, index) => new
            {
                Name = field.Key,
                ValueType = InferRecordFieldType(rows, field.Key),
                Index = index,
            })
            .Where(field => allowStructuredValues || IsRenderableTableCellType(field.ValueType))
            .Select((field, index) => new DisplayTableColumn(
                GetColumnHeader(field.Name),
                row => ShellRecordUtilities.TryGetValue(row, field.Name, out var value) ? value : null,
                Alignment: IsRightAlignedType(field.ValueType) ? DisplayTableAlignment.Right : DisplayTableAlignment.Left,
                Priority: GetPreferredColumnOrder(field.Name) + index,
                CanHide: index > 0,
                SelectionKey: field.Name))
            .ToArray();
    }

    private bool TryBuildRecordLikeColumns(IReadOnlyList<object> rows, out IReadOnlyList<DisplayTableColumn> columns, bool allowStructuredValues = false)
    {
        columns = Array.Empty<DisplayTableColumn>();

        if (rows.Count == 0 ||
            rows.Any(row =>
                !ShellRecordUtilities.IsRecordLike(row) ||
                ObjectFormatter.TryFormatSimple(row, isRoot: true, out _)))
        {
            return false;
        }

        columns = BuildRecordLikeColumns(rows, allowStructuredValues);
        return columns.Count > 0;
    }

    private static Type InferRecordFieldType(IReadOnlyList<object> rows, string fieldName)
    {
        foreach (var row in rows)
        {
            if (ShellRecordUtilities.TryGetValue(row, fieldName, out var value) && value is not null)
            {
                return value.GetType();
            }
        }

        return typeof(object);
    }

    private string RenderRecord(
        object row,
        IReadOnlyList<DisplayTableColumn> columns,
        DisplayRenderOptions options,
        int depth,
        HashSet<object> visited)
    {
        return RenderRecord(row, columns, options, depth, visited, title: null);
    }

    private string RenderRecord(
        object row,
        IReadOnlyList<DisplayTableColumn> columns,
        DisplayRenderOptions options,
        int depth,
        HashSet<object> visited,
        string? title)
    {
        var theme = TableTheme;
        var box = GetBoxCharacters(theme.BoxStyle);
        var rows = columns
            .Select(column => new RecordRow(
                column.Header,
                FormatRecordValueLines(column.ValueAccessor(row), options, depth + 1, visited),
                column.Alignment))
            .ToArray();

        if (rows.Length == 0)
        {
            return string.Empty;
        }

        var nameWidth = rows.Max(item => item.Name.Length);
        var valueWidth = rows.Max(item => item.ValueLines.Max(GetCellDisplayWidth));
        valueWidth = ApplyRecordWidthLimit(nameWidth, valueWidth, options.MaxWidth);

        var builder = new StringBuilder();

        if (string.IsNullOrWhiteSpace(title))
        {
            builder.AppendLine(BuildRecordBorder(nameWidth, valueWidth, box.TopLeft, box.TopMiddle, box.TopRight, box.Horizontal, theme));
        }
        else
        {
            EnsureRecordTitleFits(title, nameWidth, ref valueWidth, options);
            var totalWidth = nameWidth + valueWidth + 7;
            var borderSpanWidth = Math.Max(1, totalWidth - 2);
            var titleWidth = Math.Max(1, totalWidth - 4);
            builder.AppendLine(BuildSpanBorder(borderSpanWidth, box.TopLeft, box.TopRight, box.Horizontal, theme));
            builder.AppendLine(BuildSpanningRow(title, titleWidth, box, theme.Header, theme));
            builder.AppendLine(BuildRecordBorder(nameWidth, valueWidth, box.MiddleLeft, box.TopMiddle, box.MiddleRight, box.Horizontal, theme));
        }

        foreach (var recordRow in rows)
        {
            foreach (var line in BuildRecordRows(recordRow, nameWidth, valueWidth, box, theme))
            {
                builder.AppendLine(line);
            }
        }

        builder.AppendLine(BuildRecordBorder(nameWidth, valueWidth, box.BottomLeft, box.BottomMiddle, box.BottomRight, box.Horizontal, theme));
        return builder.ToString().TrimEnd();
    }

    private bool TryGetRenderableRecordColumns(object row, DisplayRenderOptions options, out IReadOnlyList<DisplayTableColumn> columns)
    {
        columns = Array.Empty<DisplayTableColumn>();

        if (row is IShellJobDisplayRow)
        {
            columns =
            [
                new DisplayTableColumn("Kind", current => ((IShellJobDisplayRow)current).Kind, MinWidth: 4, MaxWidth: 12, Priority: 0, CanHide: false),
                new DisplayTableColumn("JobId", current => ((IShellJobDisplayRow)current).JobId, DisplayTableAlignment.Right, MinWidth: 2, MaxWidth: 6, Priority: 10),
                new DisplayTableColumn("Pid", current => ((IShellJobDisplayRow)current).ProcessId, DisplayTableAlignment.Right, MinWidth: 3, MaxWidth: 8, Priority: 20),
                new DisplayTableColumn("Status", current => ((IShellJobDisplayRow)current).Status, MinWidth: 7, MaxWidth: 10, Priority: 30),
                new DisplayTableColumn("ExitCode", current => ((IShellJobDisplayRow)current).ExitCode, DisplayTableAlignment.Right, MinWidth: 4, MaxWidth: 8, Priority: 40),
                new DisplayTableColumn("Summary", current => ((IShellJobDisplayRow)current).Summary, MinWidth: 12, MaxWidth: 72, Priority: 50, CanHide: false),
            ];
            return true;
        }

        var rowType = row.GetType();
        var profile = _profiles.Resolve(rowType);

        if (profile is not null)
        {
            var context = new DisplayTableContext(rowType, [row], options);

            if (profile.TryBuildTable(context, out columns))
            {
                columns = ApplyColumnPreferences(rowType, row, columns, allowStructuredValues: true, options);
                return columns.Count > 0;
            }

            columns = Array.Empty<DisplayTableColumn>();
            return false;
        }

        if (TryBuildRecordLikeColumns([row], out columns, allowStructuredValues: true))
        {
            columns = ApplyColumnPreferences(rowType, row, columns, allowStructuredValues: true, options);
            return columns.Count > 0;
        }

        columns = BuildGenericColumns(rowType, allowStructuredValues: true, maxColumns: null);
        columns = ApplyColumnPreferences(rowType, row, columns, allowStructuredValues: true, options);
        return columns.Count > 0;
    }

    private string RenderValueList(IReadOnlyList<string> values, DisplayRenderOptions options)
    {
        var theme = TableTheme;
        var box = GetBoxCharacters(theme.BoxStyle);
        var indexWidth = Math.Max(1, (values.Count - 1).ToString().Length);
        var valueWidth = values.Count == 0 ? 0 : values.Max(GetCellDisplayWidth);
        valueWidth = ApplyListWidthLimit(indexWidth, valueWidth, options.MaxWidth);

        var builder = new StringBuilder();
        builder.AppendLine(BuildRecordBorder(indexWidth, valueWidth, box.TopLeft, box.TopMiddle, box.TopRight, box.Horizontal, theme));

        for (var index = 0; index < values.Count; index++)
        {
            foreach (var line in BuildListRowLines(index.ToString(), values[index], indexWidth, valueWidth, box, theme))
            {
                builder.AppendLine(line);
            }
        }

        builder.AppendLine(BuildRecordBorder(indexWidth, valueWidth, box.BottomLeft, box.BottomMiddle, box.BottomRight, box.Horizontal, theme));
        return builder.ToString().TrimEnd();
    }

    private static void EnsureRecordTitleFits(
        string title,
        int nameWidth,
        ref int valueWidth,
        DisplayRenderOptions options)
    {
        var currentTitleWidth = Math.Max(1, nameWidth + valueWidth + 3);
        var requiredWidth = GetCellDisplayWidth(title);

        if (requiredWidth <= currentTitleWidth)
        {
            return;
        }

        var growth = requiredWidth - currentTitleWidth;

        if (options.MaxWidth is int maxWidth && maxWidth > 0)
        {
            var currentTotalWidth = nameWidth + valueWidth + 7;
            var availableGrowth = Math.Max(0, maxWidth - currentTotalWidth);
            growth = Math.Min(growth, availableGrowth);
        }

        if (growth <= 0)
        {
            return;
        }

        valueWidth += growth;
    }

    private static IReadOnlyList<string> BuildRecordRows(
        RecordRow row,
        int nameWidth,
        int valueWidth,
        TableBoxCharacters box,
        ToshTableThemeConfig theme)
    {
        var lines = new string[row.ValueLines.Count];
        var vertical = theme.Border.Apply(box.Vertical.ToString()).ToAnsi();

        for (var index = 0; index < row.ValueLines.Count; index++)
        {
            var name = index == 0 ? row.Name : string.Empty;
            var paddedName = PadCellRight(ClipCell(name, nameWidth), nameWidth);
            var styledName = string.IsNullOrEmpty(name)
                ? paddedName
                : theme.RecordKey.Apply(paddedName).ToAnsi();
            lines[index] = $"{vertical} {styledName} {vertical} {PadRecordValue(row.ValueLines[index], row.Alignment, valueWidth)} {vertical}";
        }

        return lines;
    }

    private static IReadOnlyList<string> BuildListRowLines(
        string index,
        string value,
        int indexWidth,
        int valueWidth,
        TableBoxCharacters box,
        ToshTableThemeConfig theme)
    {
        var valueLines = SplitLines(value);
        var lines = new string[valueLines.Count];
        var vertical = theme.Border.Apply(box.Vertical.ToString()).ToAnsi();

        for (var lineIndex = 0; lineIndex < valueLines.Count; lineIndex++)
        {
            var indexText = lineIndex == 0 ? index : string.Empty;
            var styledIndex = string.IsNullOrEmpty(indexText)
                ? PadCellLeft(string.Empty, indexWidth)
                : theme.Index.Apply(PadCellLeft(ClipCell(indexText, indexWidth), indexWidth)).ToAnsi();
            lines[lineIndex] = $"{vertical} {styledIndex} {vertical} {PadCellRight(ClipCell(valueLines[lineIndex], valueWidth), valueWidth)} {vertical}";
        }

        return lines;
    }

    private static string PadRecordValue(string value, DisplayTableAlignment alignment, int width)
    {
        var clipped = ClipCell(value, width);
        return alignment == DisplayTableAlignment.Right
            ? PadCellLeft(clipped, width)
            : PadCellRight(clipped, width);
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

    private IReadOnlyList<string> FormatRecordValueLines(
        object? value,
        DisplayRenderOptions options,
        int depth,
        HashSet<object> visited)
    {
        if (value is null)
        {
            return [string.Empty];
        }

        if (_formatter.TryRenderProfile(
                value,
                new ObjectFormattingOptions(options.Style),
                DisplaySurface.RecordValue,
                out var recordValueText))
        {
            return SplitLines(recordValueText);
        }

        if (depth <= 2 &&
            TryRenderNestedRecordValue(value, options, depth, visited, out var nested))
        {
            return SplitLines(nested);
        }

        if (_formatter.TryRenderProfile(
                value,
                new ObjectFormattingOptions(options.Style),
                DisplaySurface.Nested,
                out var nestedValueText))
        {
            return SplitLines(nestedValueText);
        }

        return SplitLines(FormatTableCellValue(value, options));
    }

    private bool TryRenderNestedRecordValue(
        object value,
        DisplayRenderOptions options,
        int depth,
        HashSet<object> visited,
        out string rendered)
    {
        rendered = string.Empty;

        if (!CanRenderNestedStructuredValue(value))
        {
            return false;
        }

        var trackReference = !value.GetType().IsValueType;

        if (trackReference && !visited.Add(value))
        {
            rendered = "<cycle>";
            return true;
        }

        try
        {
            var nestedOptions = CreateNestedStructuredRenderOptions(options);
            rendered = RenderMany([value], nestedOptions, depth, visited);

            if (!rendered.Contains('\n'))
            {
                return false;
            }

            if (ShouldInlineNestedStructuredValue(rendered, nestedOptions))
            {
                rendered = string.Empty;
                return false;
            }

            return true;
        }
        finally
        {
            if (trackReference)
            {
                visited.Remove(value);
            }
        }
    }

    private bool CanRenderNestedStructuredValue(object value)
    {
        if (value is string or ShellTextLine)
        {
            return false;
        }

        if (ObjectFormatter.TryFormatSimple(value, isRoot: false, out _))
        {
            return false;
        }

        // Same rule one level down, so a scalar nested inside a record is not expanded
        // either (`TOAST-0021`).
        if (ToastRenderer.RendersAsScalar(value))
        {
            return false;
        }

        if (value is Type)
        {
            return false;
        }

        return TryGetRenderableRecordColumns(value, new DisplayRenderOptions(Style), out _) ||
               value is IEnumerable;
    }

    private static DisplayRenderOptions CreateNestedStructuredRenderOptions(DisplayRenderOptions options)
    {
        if (options.MaxWidth is not int maxWidth || maxWidth <= 0)
        {
            return options;
        }

        // Nested tables only need to budget for their own borders and padding. Halving the
        // width at every level makes deep tensors collapse much earlier than the terminal
        // actually requires, so we subtract a small structural overhead instead.
        var nestedWidth = Math.Max(18, maxWidth - 8);
        var nestedCellWidth = Math.Max(12, nestedWidth - 6);
        return options with
        {
            MaxWidth = nestedWidth,
            MaxTableCellWidth = nestedCellWidth,
            MatrixLabelDepth = options.MatrixLabelDepth + 1,
        };
    }

    private static bool ShouldInlineNestedStructuredValue(string rendered, DisplayRenderOptions options)
    {
        if (options.MaxWidth is not int maxWidth || maxWidth <= 0)
        {
            return false;
        }

        var renderedWidth = SplitLines(rendered).Max(StyledText.GetVisibleLength);
        return renderedWidth > maxWidth;
    }

    private static bool ShouldRenderSingleRecordWithTitle(Type type)
    {
        var effectiveType = Nullable.GetUnderlyingType(type) ?? type;

        return effectiveType == typeof(UnixFileMode) ||
               effectiveType == typeof(FileAttributes) ||
               effectiveType == typeof(FileSystemPrincipalInfo) ||
               effectiveType == typeof(IPAddress) ||
               effectiveType == typeof(CommandTimingInfo) ||
               effectiveType == typeof(PingReplyInfo) ||
               effectiveType == typeof(ManagedFileHandle) ||
               effectiveType == typeof(Cookie) ||
               effectiveType == typeof(CookieCollection) ||
               effectiveType == typeof(CookieContainer) ||
               effectiveType == typeof(NetworkCredential) ||
               effectiveType == typeof(PhysicalAddress) ||
               effectiveType == typeof(IPHostEntry) ||
               effectiveType == typeof(WebHeaderCollection) ||
               effectiveType == typeof(FileVersionInfo) ||
               effectiveType == typeof(DriveInfo) ||
                effectiveType == typeof(Guid) ||
               effectiveType == typeof(Version) ||
               effectiveType == typeof(byte[]) ||
               effectiveType == typeof(Uri) ||
               effectiveType == typeof(Regex) ||
               effectiveType == typeof(TimeZoneInfo) ||
               typeof(AssemblyLoadContext).IsAssignableFrom(effectiveType) ||
               typeof(ProcessStartInfo).IsAssignableFrom(effectiveType) ||
               typeof(Process).IsAssignableFrom(effectiveType) ||
               typeof(ProcessModule).IsAssignableFrom(effectiveType) ||
               typeof(FileSystemWatcher).IsAssignableFrom(effectiveType) ||
               typeof(NetworkInterface).IsAssignableFrom(effectiveType) ||
               effectiveType == typeof(Index) ||
               effectiveType == typeof(Range) ||
               effectiveType == typeof(DictionaryEntry) ||
               effectiveType == typeof(AssemblyName) ||
               typeof(Assembly).IsAssignableFrom(effectiveType) ||
               IsKeyValuePairType(effectiveType) ||
               typeof(Type).IsAssignableFrom(effectiveType) ||
               typeof(ITuple).IsAssignableFrom(effectiveType) ||
               typeof(EndPoint).IsAssignableFrom(effectiveType) ||
               typeof(HttpRequestMessage).IsAssignableFrom(effectiveType) ||
               typeof(HttpResponseMessage).IsAssignableFrom(effectiveType) ||
               typeof(HttpHeaders).IsAssignableFrom(effectiveType) ||
               typeof(HttpContent).IsAssignableFrom(effectiveType) ||
               typeof(MethodBase).IsAssignableFrom(effectiveType) ||
               typeof(PropertyInfo).IsAssignableFrom(effectiveType) ||
               typeof(FieldInfo).IsAssignableFrom(effectiveType) ||
               typeof(EventInfo).IsAssignableFrom(effectiveType) ||
               typeof(ParameterInfo).IsAssignableFrom(effectiveType) ||
               typeof(StackFrame).IsAssignableFrom(effectiveType) ||
               typeof(StackTrace).IsAssignableFrom(effectiveType) ||
               typeof(CultureInfo).IsAssignableFrom(effectiveType) ||
               typeof(Encoding).IsAssignableFrom(effectiveType) ||
               typeof(Exception).IsAssignableFrom(effectiveType) ||
               effectiveType == typeof(Dictionary<object, object?>) ||
               typeof(System.Dynamic.ExpandoObject).IsAssignableFrom(effectiveType) ||
               typeof(Stream).IsAssignableFrom(effectiveType) ||
               effectiveType == typeof(ZipArchive) ||
               effectiveType == typeof(ZipArchiveEntry) ||
               effectiveType == typeof(OperatingSystem) ||
               effectiveType == typeof(RuntimeInformationSnapshot) ||
               effectiveType == typeof(X509Certificate2) ||
               effectiveType == typeof(ClaimsIdentity) ||
               effectiveType == typeof(ClaimsPrincipal) ||
               effectiveType == typeof(Claim) ||
               effectiveType == typeof(Vector2) ||
               effectiveType == typeof(Vector3) ||
               effectiveType == typeof(Vector4) ||
               effectiveType == typeof(Quaternion) ||
               effectiveType == typeof(Matrix4x4) ||
               effectiveType == typeof(WebProxy);
    }

    private static string GetSingleRecordTitle(Type type)
    {
        var effectiveType = Nullable.GetUnderlyingType(type) ?? type;

        if (typeof(MethodInfo).IsAssignableFrom(effectiveType))
        {
            return ObjectFormatter.GetTypeName(typeof(MethodInfo));
        }

        if (typeof(ConstructorInfo).IsAssignableFrom(effectiveType))
        {
            return ObjectFormatter.GetTypeName(typeof(ConstructorInfo));
        }

        if (typeof(PropertyInfo).IsAssignableFrom(effectiveType))
        {
            return ObjectFormatter.GetTypeName(typeof(PropertyInfo));
        }

        if (typeof(FieldInfo).IsAssignableFrom(effectiveType))
        {
            return ObjectFormatter.GetTypeName(typeof(FieldInfo));
        }

        if (typeof(EventInfo).IsAssignableFrom(effectiveType))
        {
            return ObjectFormatter.GetTypeName(typeof(EventInfo));
        }

        if (typeof(ParameterInfo).IsAssignableFrom(effectiveType))
        {
            return ObjectFormatter.GetTypeName(typeof(ParameterInfo));
        }

        if (typeof(Type).IsAssignableFrom(effectiveType))
        {
            return ObjectFormatter.GetTypeName(typeof(Type));
        }

        if (typeof(NetworkInterface).IsAssignableFrom(effectiveType))
        {
            return ObjectFormatter.GetTypeName(typeof(NetworkInterface));
        }

        if (effectiveType == typeof(Dictionary<object, object?>))
        {
            return "Dictionary";
        }

        if (typeof(System.Dynamic.ExpandoObject).IsAssignableFrom(effectiveType))
        {
            return "Record";
        }

        if (typeof(Stream).IsAssignableFrom(effectiveType))
        {
            return ObjectFormatter.GetTypeName(effectiveType);
        }

        if (effectiveType == typeof(RuntimeInformationSnapshot))
        {
            return "Runtime";
        }

        return ObjectFormatter.GetTypeName(effectiveType);
    }

    private static bool IsKeyValuePairType(Type type)
    {
        return type.IsGenericType &&
               type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>);
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
               effectiveType == typeof(decimal) ||
               typeof(Units.Quantity).IsAssignableFrom(effectiveType);
    }

    private sealed record RecordRow(string Name, IReadOnlyList<string> ValueLines, DisplayTableAlignment Alignment);

}
