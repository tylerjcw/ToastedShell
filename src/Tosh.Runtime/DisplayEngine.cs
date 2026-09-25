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
    private readonly ObjectFormatter _formatter;
    private readonly DisplayProfileRegistry _profiles;

    public DisplayEngine(ObjectFormatter formatter)
    {
        _formatter = formatter;
        _profiles = formatter.Profiles;
        TableTheme = new ToshTableThemeConfig();
    }

    public ObjectRenderStyle Style
    {
        get => _formatter.Style;
        set => _formatter.Style = value;
    }

    public ToshTableThemeConfig TableTheme { get; set; }

    public DisplayPreferences? Preferences { get; set; }

    /// <summary>
    /// Optional code highlighter used by per-type renderers (e.g. the help-topic
    /// example block) to colorise pipeline code in the same style as the REPL.
    /// Set by the CLI when syntax highlighting is enabled; null otherwise.
    /// </summary>
    public Func<string, string>? CodeHighlighter { get; set; }

    public string Render(object? value)
    {
        return Render(value, new DisplayRenderOptions(Style));
    }

    public string Render(object? value, DisplayRenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return RenderMany([value], options, depth: 0, new HashSet<object>(ReferenceEqualityComparer.Instance));
    }

    public string RenderMany(IReadOnlyList<object?> values)
    {
        return RenderMany(values, new DisplayRenderOptions(Style));
    }

    public string RenderMany(IReadOnlyList<object?> values, DisplayRenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(options);

        // Top-level: if the sole rendered value is a runtime-namespace summary source
        // (e.g. `$tosh`), render via the dedicated multi-section summary renderer.
        if (values.Count == 1 && values[0] is IShellRuntimeNamespaceSummarySource summarySource)
        {
            return RuntimeNamespaceSummaryRenderer.Render(summarySource.GetDisplaySummary());
        }

        // Single HelpTopic gets a custom rich layout (mirrors the $tosh design),
        // including syntax-highlighted examples when a CodeHighlighter is wired up.
        // Several HelpTopics fall through to the standard table profile on purpose — a table is
        // how two topics are compared, and it reads well.
        if (values.Count == 1 && values[0] is HelpTopic helpTopic)
        {
            return HelpTopicSummaryRenderer.Render(helpTopic, CodeHighlighter);
        }

        // `TS-P2-63`. A HelpTopic *among other values* had neither rendering: the table profile
        // needs uniform shapes, so a mixed batch fell to the generic container path and the topic
        // came out as a bare `[HelpTopic]` header. `help ls` alone panelled; `echo one` followed
        // by `help ls` did not, which made the panel look like a property of being first rather
        // than of being a help topic. A mixed batch now renders value by value, so each one gets
        // whatever rendering it would have had on its own.
        if (values.Count > 1 && ContainsMixedBespokeValue(values))
        {
            return string.Join(
                Environment.NewLine,
                values.Select(value => RenderMany([value], options)));
        }

        return RenderMany(values, options, depth: 0, new HashSet<object>(ReferenceEqualityComparer.Instance));
    }

    /// <summary>
    /// True when the batch holds a value with its own top-level rendering alongside values that
    /// do not share its shape.
    /// </summary>
    /// <remarks>
    /// A batch of nothing but help topics is left alone — that is the comparison table, and it is
    /// deliberate. This is only about the mixed case, where a table cannot represent all of them
    /// and the bespoke value is the one that loses.
    /// </remarks>
    private static bool ContainsMixedBespokeValue(IReadOnlyList<object?> values)
    {
        var bespoke = 0;

        foreach (var value in values)
        {
            if (value is HelpTopic or IShellRuntimeNamespaceSummarySource)
            {
                bespoke++;
            }
        }

        return bespoke > 0 && bespoke != values.Count;
    }

    /// <summary>
    /// Builds a pre-computed table plan for the given items using the same column resolution,
    /// cell formatting, and width-fitting logic as the normal table renderer.
    /// Returns null if the items don't have renderable table columns.
    /// </summary>
    public InlineTablePlan? BuildInlineTablePlan(IReadOnlyList<object?> items, DisplayRenderOptions? options = null)
    {
        if (items.Count == 0)
        {
            return null;
        }

        var renderOptions = options ?? new DisplayRenderOptions(Style);

        if (!TryGetRenderableColumns(items, renderOptions, out var rows, out var columns))
        {
            return null;
        }

        var effectiveColumns = BuildEffectiveColumns(rows, columns, includeIndexColumn: true);

        var rawCells = rows
            .Select((row, index) => BuildRowCells(index, row, columns, renderOptions, includeIndexColumn: true))
            .ToArray();

        var visibleColumns = BuildVisibleColumns(effectiveColumns, rawCells, renderOptions);

        if (visibleColumns.Count == 0)
        {
            return null;
        }

        var planColumns = visibleColumns
            .Select(vc => new InlineTableColumn(
                ClipCell(vc.Column.Header, vc.Width),
                vc.Width,
                vc.Column.Alignment,
                vc.Column.UseHeaderTheme,
                vc.Column.UseIndexTheme))
            .ToArray();

        var planRows = rawCells
            .Select(cells => visibleColumns.Select(vc => cells[vc.ColumnIndex]).ToArray())
            .ToArray();

        return new InlineTablePlan(planColumns, planRows, TableTheme.BoxStyle, TableTheme);
    }

    /// <summary>
    /// Tries to resolve renderable columns for streaming output from the first row only.
    /// Returns false if the row type has no table representation.
    /// </summary>
    public bool TryBuildStreamingColumns(object firstRow, DisplayRenderOptions options, out IReadOnlyList<DisplayTableColumn> columns)
        => TryGetRenderableColumns([firstRow], options, out _, out columns);

    /// <summary>
    /// Formats a single value for display in a streaming table cell.
    /// </summary>
    public string FormatStreamingCellValue(object? value, DisplayRenderOptions options)
        => FormatTableCellValue(value, options);

    private string RenderMany(
        IReadOnlyList<object?> values,
        DisplayRenderOptions options,
        int depth,
        HashSet<object> visited)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(visited);

        if (values.Count == 0)
        {
            return string.Empty;
        }

        if (TryRenderPlainTextLines(values, out var plainText))
        {
            return plainText;
        }

        if (TryRenderSingleDetailedProfileValue(values, options, depth, visited, out var detailedProfileValue))
        {
            return detailedProfileValue;
        }

        if (TryRenderSingleProfileBackedScalarValue(values, options, depth, out var profileScalar))
        {
            return profileScalar;
        }

        if (TryRenderSingleEnumValue(values, options, out var enumValue))
        {
            return enumValue;
        }

        if (TryRenderEnumType(values, options, out var enumType))
        {
            return enumType;
        }

        if (TryRenderSingleScalarValue(values, options, depth, out var scalar))
        {
            return scalar;
        }

        if (TryRenderSingleMatrix(values, options, out var matrix))
        {
            return matrix;
        }

        if (TryRenderSingleEnumerable(values, options, depth, visited, out var enumerable))
        {
            return enumerable;
        }

        if (TryRenderRecord(values, options, depth, visited, out var record))
        {
            return record;
        }

        if (TryRenderTable(values, options, depth, out var table))
        {
            return table;
        }

        if (TryRenderMixedTypeGroups(values, options, depth, visited, out var grouped))
        {
            return grouped;
        }

        if (TryRenderValueList(values, options, out var list))
        {
            return list;
        }

        var rendered = values.Select(value => RenderSingleValueFallback(value, options)).ToList();
        var separator = rendered.Any(item => item.Contains('\n'))
            ? $"{Environment.NewLine}{Environment.NewLine}"
            : Environment.NewLine;

        return string.Join(separator, rendered);
    }

    private bool TryRenderMixedTypeGroups(
        IReadOnlyList<object?> values,
        DisplayRenderOptions options,
        int depth,
        HashSet<object> visited,
        out string rendered)
    {
        rendered = string.Empty;

        if (values.Count <= 1)
        {
            return false;
        }

        var groups = BuildMixedTypeGroups(values);

        if (groups.Count <= 1)
        {
            return false;
        }

        var sections = new List<string>(groups.Count);

        foreach (var group in groups)
        {
            var groupValues = group.Values;
            string groupRendered;
            var suppressHeading = !group.IsNullGroup &&
                                  ((groupValues.Count == 1 &&
                                    ShouldRenderMixedGroupAsStandaloneBlock(groupValues[0], options, depth, visited)) ||
                                   (groupValues.Count > 1 &&
                                    groupValues[0] is not null &&
                                    groupValues[0] is not System.Dynamic.ExpandoObject &&
                                    ShouldRenderSingleRecordWithTitle(groupValues[0]!.GetType())));

            if (suppressHeading)
            {
                groupRendered = RenderMany(groupValues, options, depth, visited);
            }
            else if (group.IsNullGroup)
            {
                groupRendered = RenderMany(
                    Enumerable.Repeat<object?>("null", groupValues.Count).ToArray(),
                    options,
                    depth + 1,
                    visited);
            }
            else
            {
                groupRendered = RenderMany(groupValues, options, depth + 1, visited);
            }

            if (string.IsNullOrWhiteSpace(groupRendered))
            {
                continue;
            }

            if (suppressHeading)
            {
                sections.Add(groupRendered);
            }
            else
            {
                var heading = TableTheme.RecordKey.Apply($"[{group.DisplayName}]").ToAnsi();
                sections.Add($"{heading}{Environment.NewLine}{groupRendered}");
            }
        }

        if (sections.Count <= 1)
        {
            return false;
        }

        rendered = string.Join($"{Environment.NewLine}{Environment.NewLine}", sections);
        return true;
    }

    private bool ShouldRenderMixedGroupAsStandaloneBlock(
        object? value,
        DisplayRenderOptions options,
        int depth,
        HashSet<object> visited)
    {
        if (depth > 0 || value is null)
        {
            return false;
        }

        object?[] singleton = [value];

        return TryRenderSingleDetailedProfileValue(singleton, options, depth, visited, out _) ||
               TryRenderSingleProfileBackedScalarValue(singleton, options, depth, out _) ||
               TryRenderSingleEnumValue(singleton, options, out _) ||
               TryRenderEnumType(singleton, options, out _) ||
               TryRenderSingleScalarValue(singleton, options, depth, out _);
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

    private bool TryRenderSingleEnumValue(IReadOnlyList<object?> values, DisplayRenderOptions options, out string rendered)
    {
        rendered = string.Empty;

        if (values.Count != 1 || values[0] is not Enum enumValue)
        {
            return false;
        }

        var numericValue = FormatTableCellValue(
            ReflectionMetadataUtilities.GetEnumNumericValue(enumValue),
            options);
        var nameValue = ReflectionMetadataUtilities.FormatEnumValue(enumValue, includeTypeName: false);

        if (string.IsNullOrWhiteSpace(nameValue))
        {
            nameValue = numericValue;
        }

        rendered = RenderTitledTable(
            ReflectionMetadataUtilities.FormatEnumValue(enumValue, includeTypeName: true),
            [
                new DisplayTableColumn("#", _ => null, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: options.MaxTableCellWidth, Priority: 0, CanHide: false),
                new DisplayTableColumn("Name", _ => null, MinWidth: 4, MaxWidth: options.MaxTableCellWidth, Priority: 10, CanHide: false),
            ],
            [[numericValue, nameValue]],
            options,
            includeHeader: false);
        return true;
    }

    private bool TryRenderEnumType(IReadOnlyList<object?> values, DisplayRenderOptions options, out string rendered)
    {
        rendered = string.Empty;

        if (values.Count != 1 || values[0] is not Type type || !type.IsEnum)
        {
            return false;
        }

        var rows = Enum
            .GetNames(type)
            .Select(name =>
            {
                var parsed = (Enum)Enum.Parse(type, name);
                var numericText = FormatTableCellValue(
                    ReflectionMetadataUtilities.GetEnumNumericValue(parsed),
                    options);
                return new[] { numericText, name };
            })
            .ToArray();

        if (rows.Length == 0)
        {
            return false;
        }

        rendered = RenderTitledTable(
            ReflectionMetadataUtilities.GetDisplayName(type),
            [
                new DisplayTableColumn("#", _ => null, DisplayTableAlignment.Right, MinWidth: 1, MaxWidth: options.MaxTableCellWidth, Priority: 0, CanHide: false),
                new DisplayTableColumn("Name", _ => null, MinWidth: 4, MaxWidth: options.MaxTableCellWidth, Priority: 10, CanHide: false),
            ],
            rows,
            options,
            includeHeader: true);
        return true;
    }

    private bool TryRenderSingleScalarValue(
        IReadOnlyList<object?> values,
        DisplayRenderOptions options,
        int depth,
        out string rendered)
    {
        rendered = string.Empty;

        if (depth > 0 || values.Count != 1 || values[0] is null)
        {
            return false;
        }

        if (!TryFormatPrettyScalar(values[0]!, out var typeName, out var valueText, out var valueAlignment))
        {
            return false;
        }

        rendered = RenderScalarValueTable(typeName, valueText, valueAlignment, options);
        return true;
    }

    private bool TryRenderSingleDetailedProfileValue(
        IReadOnlyList<object?> values,
        DisplayRenderOptions options,
        int depth,
        HashSet<object> visited,
        out string rendered)
    {
        rendered = string.Empty;

        if (depth > 0 || values.Count != 1 || values[0] is null)
        {
            return false;
        }

        var row = values[0]!;
        var rowType = row.GetType();

        if (row is Type typeValue && typeValue.IsEnum)
        {
            return false;
        }

        if (!ShouldRenderSingleRecordWithTitle(rowType) ||
            !TryGetRenderableRecordColumns(row, options, out var columns))
        {
            return false;
        }

        var title = ShellRecordUtilities.TryGetTsspTitle(row) ?? GetSingleRecordTitle(rowType);
        rendered = RenderRecord(row, columns, options, depth, visited, title);
        return true;
    }

    private bool TryRenderSingleProfileBackedScalarValue(
        IReadOnlyList<object?> values,
        DisplayRenderOptions options,
        int depth,
        out string rendered)
    {
        rendered = string.Empty;

        if (depth > 0 || values.Count != 1 || values[0] is null)
        {
            return false;
        }

        if (!TryFormatProfileBackedPrettyScalar(values[0]!, options, out var typeName, out var valueText, out var valueAlignment))
        {
            return false;
        }

        rendered = RenderScalarValueTable(typeName, valueText, valueAlignment, options);
        return true;
    }

    private bool TryRenderSingleEnumerable(
        IReadOnlyList<object?> values,
        DisplayRenderOptions options,
        int depth,
        HashSet<object> visited,
        out string rendered)
    {
        rendered = string.Empty;

        if (values.Count != 1 ||
            values[0] is null ||
            values[0] is string ||
            ShellRecordUtilities.IsRecordLike(values[0]) ||
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

        rendered = RenderMany(items, options, depth + 1, visited);
        return true;
    }

    private string RenderSingleValueFallback(object? value, DisplayRenderOptions options)
    {
        return _formatter.Format(value, new ObjectFormattingOptions(options.Style));
    }

    private bool TryRenderRecord(
        IReadOnlyList<object?> values,
        DisplayRenderOptions options,
        int depth,
        HashSet<object> visited,
        out string table)
    {
        table = string.Empty;

        if (values.Count != 1 || values[0] is null)
        {
            return false;
        }

        var row = values[0]!;
        var rowType = row.GetType();
        var isSingleTree = row is IDisplayTreeNode treeNode && treeNode.GetDisplayChildren().Any();

        if (isSingleTree || ShouldPreferTableForSingleItem(rowType))
        {
            return false;
        }

        if (depth > 0 && rowType != typeof(System.Dynamic.ExpandoObject) && ShouldRenderSingleRecordWithTitle(rowType))
        {
            return false;
        }

        if (!TryGetRenderableRecordColumns(row, options, out var columns))
        {
            return false;
        }

        var title = ShellRecordUtilities.TryGetTsspTitle(row)
            ?? (ShouldRenderSingleRecordWithTitle(rowType) ? GetSingleRecordTitle(rowType) : null);
        table = RenderRecord(row, columns, options, depth, visited, title);
        return true;
    }

    private bool TryRenderTable(IReadOnlyList<object?> values, DisplayRenderOptions options, int depth, out string table)
    {
        table = string.Empty;

        var allowSingle = values.Count == 1 && values[0] is not null &&
                          (values[0] is IDisplayTreeNode treeNode && treeNode.GetDisplayChildren().Any() ||
                           ShouldPreferTableForSingleItem(values[0]!.GetType()));

        if (values.Count == 0 || (values.Count <= 1 && !allowSingle))
        {
            return false;
        }

        if (ShouldPreferValueList(values))
        {
            return false;
        }

        if (!TryGetRenderableColumns(values, options, out var rows, out var columns))
        {
            return false;
        }

        string? title = null;

        if (depth == 0 && values.Count > 1)
        {
            var firstType = values[0]?.GetType();

            if (firstType is not null &&
                values.All(v => v is not null && v.GetType() == firstType) &&
                ShouldRenderSingleRecordWithTitle(firstType))
            {
                title = GetSingleRecordTitle(firstType);
            }
        }

        table = RenderTable(rows, columns, options, includeIndexColumn: !allowSingle, title: title);
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

    private static IReadOnlyList<MixedTypeGroup> BuildMixedTypeGroups(IReadOnlyList<object?> values)
    {
        var groups = new List<MixedTypeGroup>();

        foreach (var value in values)
        {
            var key = GetMixedTypeGroupKey(value);

            if (groups.Count > 0 && string.Equals(groups[^1].Identity, key.Identity, StringComparison.Ordinal))
            {
                groups[^1].Values.Add(value);
            }
            else
            {
                var group = new MixedTypeGroup(key.Identity, key.DisplayName, key.IsNullGroup);
                group.Values.Add(value);
                groups.Add(group);
            }
        }

        return groups;
    }

    private static MixedTypeGroupKey GetMixedTypeGroupKey(object? value)
    {
        if (value is null)
        {
            return new MixedTypeGroupKey("null", "null", IsNullGroup: true);
        }

        if (value is IShellTypeDescriptor descriptor)
        {
            return new MixedTypeGroupKey(descriptor.ShellFullName, descriptor.ShellTypeName, IsNullGroup: false);
        }

        if (value is IShellTypedObject typed)
        {
            return new MixedTypeGroupKey(typed.ShellTypeDescriptor.ShellFullName, typed.ShellTypeDescriptor.ShellTypeName, IsNullGroup: false);
        }

        if (BuiltInShellTypes.TryDescribeRuntimeValue(value, out var builtInDescriptor))
        {
            return new MixedTypeGroupKey(builtInDescriptor.ShellFullName, builtInDescriptor.ShellTypeName, IsNullGroup: false);
        }

        if (value is IShellRecordObject shellRecord)
        {
            return new MixedTypeGroupKey(shellRecord.ShellTypeName, shellRecord.ShellTypeName, IsNullGroup: false);
        }

        var runtimeType = value.GetType();

        if (IsMixedNumericScalarType(runtimeType))
        {
            return new MixedTypeGroupKey("scalar:number", "number", IsNullGroup: false);
        }

        return new MixedTypeGroupKey(runtimeType.FullName ?? runtimeType.Name, ObjectFormatter.GetTypeName(runtimeType), IsNullGroup: false);
    }

    private sealed class MixedTypeGroup(string identity, string displayName, bool isNullGroup)
    {
        public string Identity { get; } = identity;

        public string DisplayName { get; } = displayName;

        public bool IsNullGroup { get; } = isNullGroup;

        public List<object?> Values { get; } = [];
    }

    private readonly record struct MixedTypeGroupKey(string Identity, string DisplayName, bool IsNullGroup);


}
