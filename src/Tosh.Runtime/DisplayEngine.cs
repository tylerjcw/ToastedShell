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

public sealed class DisplayEngine
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
        // Multiple HelpTopics fall through to the standard table profile.
        if (values.Count == 1 && values[0] is HelpTopic helpTopic)
        {
            return HelpTopicSummaryRenderer.Render(helpTopic, CodeHighlighter);
        }

        return RenderMany(values, options, depth: 0, new HashSet<object>(ReferenceEqualityComparer.Instance));
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

        rendered = RenderRecord(row, columns, options, depth, visited, GetSingleRecordTitle(rowType));
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

    private bool TryRenderSingleMatrix(
        IReadOnlyList<object?> values,
        DisplayRenderOptions options,
        out string rendered)
    {
        rendered = string.Empty;
        object? matrixSource = values.Count == 1 ? values[0] : values;

        if (!TryBuildMatrixSections(matrixSource, [], out var sections))
        {
            return false;
        }

        string? nested = null;
        string? flattened = null;

        if (!options.PreferTensorSlices &&
            sections.Count > 1 &&
            TryRenderNestedTensor(matrixSource, options, out var nestedCandidate))
        {
            nested = nestedCandidate;
        }

        if (sections.Count > 1 &&
            TryRenderFlattenedTensor(sections, options, out var flattenedCandidate))
        {
            flattened = flattenedCandidate;
        }

        if (nested is not null)
        {
            rendered = nested;
            return true;
        }

        if (flattened is not null)
        {
            rendered = flattened;
            return true;
        }

        rendered = RenderMatrixSections(sections, options);
        return !string.IsNullOrWhiteSpace(rendered);
    }

    private bool TryRenderNestedTensor(
        object? value,
        DisplayRenderOptions options,
        out string rendered)
    {
        rendered = string.Empty;

        if (!TryBuildNestedTensorSection(value, out var section))
        {
            return false;
        }

        var columnCount = Math.Max(1, section.Rows.Max(row => row.Count));
        var initialCellWidthBudget = GetNestedTensorCellWidthBudget(columnCount, options);

        if (TryRenderNestedTensorCandidate(section, options, initialCellWidthBudget, out rendered))
        {
            return true;
        }

        var minimumBudget = 12;

        if (initialCellWidthBudget <= minimumBudget)
        {
            rendered = string.Empty;
            return false;
        }

        for (var budget = initialCellWidthBudget - 1; budget >= minimumBudget; budget--)
        {
            if (TryRenderNestedTensorCandidate(section, options, budget, out var candidate))
            {
                rendered = candidate;
                return true;
            }
        }

        rendered = string.Empty;
        return false;
    }

    private bool TryRenderNestedTensorCandidate(
        MatrixDisplaySection section,
        DisplayRenderOptions options,
        int cellWidthBudget,
        out string rendered)
    {
        rendered = string.Empty;

        if (!TryMaterializeTensorSection(section, options, cellWidthBudget, out var materializedSection))
        {
            return false;
        }

        var candidate = RenderMatrixSection(materializedSection, options);

        if (candidate.Contains('…'))
        {
            return false;
        }

        if (options.MaxWidth is int candidateMaxWidth &&
            candidateMaxWidth > 0 &&
            SplitLines(candidate).Max(StyledText.GetVisibleLength) > candidateMaxWidth)
        {
            return false;
        }

        rendered = candidate;
        return true;
    }

    private bool TryMaterializeTensorSection(
        MatrixDisplaySection section,
        DisplayRenderOptions options,
        int cellWidthBudget,
        out MatrixDisplaySection materializedSection)
    {
        materializedSection = section;

        var rows = new List<IReadOnlyList<object?>>(section.Rows.Count);

        foreach (var row in section.Rows)
        {
            var cells = new object?[row.Count];

            for (var index = 0; index < row.Count; index++)
            {
                var cell = row[index];

                if (!CanRenderAsMatrixLikeValue(cell) ||
                    !TryRenderTensorCellValue(cell!, cellWidthBudget, options, out var renderedCell))
                {
                    return false;
                }

                cells[index] = renderedCell;
            }

            rows.Add(cells);
        }

        materializedSection = section with { Rows = rows };
        return true;
    }

    private static int GetNestedTensorCellWidthBudget(int columnCount, DisplayRenderOptions options)
    {
        if (options.MaxWidth is not int maxWidth || maxWidth <= 0)
        {
            return Math.Max(24, options.MaxTableCellWidth);
        }

        // Let nested tensor cells compete for as much of the available width as they can use.
        // We decide whether that wider child layout is acceptable by checking the rendered
        // parent section afterward, rather than pessimistically dividing width up front.
        return Math.Max(12, maxWidth - 8);
    }

    private bool TryRenderTensorCellValue(
        object value,
        int widthBudget,
        DisplayRenderOptions options,
        out string rendered)
    {
        rendered = string.Empty;

        var nestedOptions = options with
        {
            MaxWidth = widthBudget,
            MaxTableCellWidth = Math.Max(12, widthBudget),
            MatrixLabelDepth = options.MatrixLabelDepth + 1,
        };
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);

        if (!value.GetType().IsValueType)
        {
            visited.Add(value);
        }

        rendered = RenderMany([value], nestedOptions, depth: 1, visited);

        if ((!rendered.Contains('\n') ||
             SplitLines(rendered).Max(StyledText.GetVisibleLength) > widthBudget ||
             rendered.Contains('…')) &&
            TryRenderTensorSliceFallback(value, nestedOptions, widthBudget, out var sliceRendered))
        {
            rendered = sliceRendered;
        }

        if (!rendered.Contains('\n') ||
            SplitLines(rendered).Max(StyledText.GetVisibleLength) > widthBudget ||
            rendered.Contains('…'))
        {
            rendered = string.Empty;
            return false;
        }

        return true;
    }

    private bool TryRenderTensorSliceFallback(
        object value,
        DisplayRenderOptions options,
        int widthBudget,
        out string rendered)
    {
        rendered = string.Empty;
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);

        if (!value.GetType().IsValueType)
        {
            visited.Add(value);
        }

        rendered = RenderMany([value], options with { PreferTensorSlices = true }, depth: 1, visited);
        return rendered.Contains('\n') &&
               SplitLines(rendered).Max(StyledText.GetVisibleLength) <= widthBudget &&
               !rendered.Contains('…');
    }

    private bool TryRenderFlattenedTensor(
        IReadOnlyList<MatrixDisplaySection> sections,
        DisplayRenderOptions options,
        out string rendered)
    {
        rendered = string.Empty;

        if (!TryBuildFlattenedTensorSource(sections, out var rank, out var axisLengths, out var cells) ||
            rank < 3)
        {
            return false;
        }

        string? bestCandidate = null;
        var bestWidth = 0;

        for (var rowAxisCount = 1; rowAxisCount < rank; rowAxisCount++)
        {
            if (!TryRenderFlattenedTensorCandidate(cells, axisLengths, rowAxisCount, options, out var candidate))
            {
                continue;
            }

            var candidateWidth = SplitLines(candidate).Max(StyledText.GetVisibleLength);

            if (candidateWidth <= bestWidth)
            {
                continue;
            }

            bestCandidate = candidate;
            bestWidth = candidateWidth;
        }

        if (string.IsNullOrWhiteSpace(bestCandidate))
        {
            return false;
        }

        rendered = bestCandidate;
        return true;
    }

    private bool TryBuildFlattenedTensorSource(
        IReadOnlyList<MatrixDisplaySection> sections,
        out int rank,
        out int[] axisLengths,
        out IReadOnlyList<FlattenedTensorCell> cells)
    {
        rank = 0;
        axisLengths = Array.Empty<int>();
        cells = Array.Empty<FlattenedTensorCell>();

        if (sections.Count == 0)
        {
            return false;
        }

        rank = sections.Max(section => section.SlicePath.Count) + 2;
        axisLengths = new int[rank];
        var builtCells = new List<FlattenedTensorCell>();

        foreach (var section in sections)
        {
            for (var rowIndex = 0; rowIndex < section.Rows.Count; rowIndex++)
            {
                var row = section.Rows[rowIndex];

                for (var columnIndex = 0; columnIndex < row.Count; columnIndex++)
                {
                    var indices = new int[rank];

                    for (var index = 0; index < section.SlicePath.Count && index < rank - 2; index++)
                    {
                        indices[index] = section.SlicePath[index];
                    }

                    indices[rank - 2] = rowIndex;
                    indices[rank - 1] = columnIndex;

                    for (var axis = 0; axis < indices.Length; axis++)
                    {
                        axisLengths[axis] = Math.Max(axisLengths[axis], indices[axis] + 1);
                    }

                    builtCells.Add(new FlattenedTensorCell(indices, row[columnIndex]));
                }
            }
        }

        if (builtCells.Count == 0 || axisLengths.Any(length => length <= 0))
        {
            return false;
        }

        cells = builtCells;
        return true;
    }

    private bool TryRenderFlattenedTensorCandidate(
        IReadOnlyList<FlattenedTensorCell> cells,
        IReadOnlyList<int> axisLengths,
        int rowAxisCount,
        DisplayRenderOptions options,
        out string rendered)
    {
        rendered = string.Empty;
        var columnAxisCount = axisLengths.Count - rowAxisCount;

        if (rowAxisCount <= 0 || columnAxisCount <= 0)
        {
            return false;
        }

        var rowAxisLengths = axisLengths.Take(rowAxisCount).ToArray();
        var columnAxisLengths = axisLengths.Skip(rowAxisCount).ToArray();
        var rowCoordinates = EnumerateIndexVectors(rowAxisLengths).ToArray();
        var columnCoordinates = EnumerateIndexVectors(columnAxisLengths).ToArray();

        if (rowCoordinates.Length == 0 || columnCoordinates.Length == 0)
        {
            return false;
        }

        var valuesByIndex = cells.ToDictionary(
            cell => BuildFlattenedTensorCellKey(cell.Indices),
            cell => cell.Value,
            StringComparer.Ordinal);
        var rowAxisLabels = BuildGroupedTensorAxisLabels(rowCoordinates, startDepth: 0);
        var columnHeaders = BuildGroupedTensorAxisHeaders(columnCoordinates, startDepth: rowAxisCount);

        var rows = rowCoordinates
            .Select((rowIndices, rowIndex) =>
            {
                var values = new object?[columnCoordinates.Length];

                for (var columnIndex = 0; columnIndex < columnCoordinates.Length; columnIndex++)
                {
                    var fullIndices = new int[axisLengths.Count];
                    Array.Copy(rowIndices, 0, fullIndices, 0, rowIndices.Length);
                    Array.Copy(columnCoordinates[columnIndex], 0, fullIndices, rowIndices.Length, columnCoordinates[columnIndex].Length);
                    values[columnIndex] = valuesByIndex.TryGetValue(BuildFlattenedTensorCellKey(fullIndices), out var value)
                        ? value
                        : null;
                }

                return new FlattenedTensorRow(
                    rowAxisLabels[rowIndex],
                    values);
            })
            .Cast<object>()
            .ToArray();

        var columns = new List<DisplayTableColumn>(rowAxisCount + columnCoordinates.Length);

        for (var axisIndex = 0; axisIndex < rowAxisCount; axisIndex++)
        {
            var capturedAxisIndex = axisIndex;
            columns.Add(new DisplayTableColumn(
                string.Empty,
                row => ((FlattenedTensorRow)row).TryGetAxisLabel(capturedAxisIndex, out var label) ? label : string.Empty,
                Alignment: DisplayTableAlignment.Left,
                MinWidth: 1,
                MaxWidth: options.MaxTableCellWidth,
                Priority: axisIndex,
                CanHide: false,
                UseHeaderTheme: false,
                UseIndexTheme: false));
        }

        for (var columnIndex = 0; columnIndex < columnCoordinates.Length; columnIndex++)
        {
            var capturedIndex = columnIndex;
            columns.Add(new DisplayTableColumn(
                columnHeaders[columnIndex],
                row => ((FlattenedTensorRow)row).Values[capturedIndex],
                Alignment: InferFlattenedTensorColumnAlignment(rows, capturedIndex),
                MinWidth: 1,
                MaxWidth: options.MaxTableCellWidth,
                Priority: 10 + rowAxisCount + columnIndex,
                CanHide: false,
                UseHeaderTheme: false));
        }

        var candidate = RenderTable(rows, columns, options, includeIndexColumn: false);

        if (candidate.Contains('…'))
        {
            return false;
        }

        if (options.MaxWidth is int maxWidth &&
            maxWidth > 0 &&
            SplitLines(candidate).Max(StyledText.GetVisibleLength) > maxWidth)
        {
            return false;
        }

        rendered = candidate;
        return true;
    }

    private static string BuildFlattenedTensorCellKey(IReadOnlyList<int> indices)
    {
        return string.Join("|", indices.Select(index => index.ToString(CultureInfo.InvariantCulture)));
    }

    private static DisplayTableAlignment InferFlattenedTensorColumnAlignment(
        IReadOnlyList<object> rows,
        int columnIndex)
    {
        foreach (var row in rows.Cast<FlattenedTensorRow>())
        {
            if (columnIndex < row.Values.Count && row.Values[columnIndex] is not null)
            {
                return IsRightAlignedType(row.Values[columnIndex]!.GetType())
                    ? DisplayTableAlignment.Right
                    : DisplayTableAlignment.Left;
            }
        }

        return DisplayTableAlignment.Left;
    }

    private IReadOnlyList<IReadOnlyList<string>> BuildGroupedTensorAxisLabels(
        IReadOnlyList<int[]> coordinates,
        int startDepth)
    {
        if (coordinates.Count == 0)
        {
            return Array.Empty<IReadOnlyList<string>>();
        }

        var result = new List<IReadOnlyList<string>>(coordinates.Count);
        int[]? previous = null;

        foreach (var coordinate in coordinates)
        {
            var labels = new string[coordinate.Length];

            for (var axisIndex = 0; axisIndex < coordinate.Length; axisIndex++)
            {
                var depth = startDepth + axisIndex;
                var label = StyleMatrixAxisLabel(FormatMatrixAxisLabel(coordinate[axisIndex], depth), depth);
                labels[axisIndex] = previous is not null &&
                                    CoordinatesSharePrefix(previous, coordinate, axisIndex + 1)
                    ? string.Empty
                    : label;
            }

            result.Add(labels);
            previous = coordinate;
        }

        return result;
    }

    private string[] BuildGroupedTensorAxisHeaders(
        IReadOnlyList<int[]> coordinates,
        int startDepth)
    {
        if (coordinates.Count == 0)
        {
            return Array.Empty<string>();
        }

        var headers = new string[coordinates.Count];
        int[]? previous = null;

        for (var coordinateIndex = 0; coordinateIndex < coordinates.Count; coordinateIndex++)
        {
            var coordinate = coordinates[coordinateIndex];
            var lines = new string[coordinate.Length];

            for (var axisIndex = 0; axisIndex < coordinate.Length; axisIndex++)
            {
                var depth = startDepth + axisIndex;
                var label = StyleMatrixAxisLabel(FormatMatrixAxisLabel(coordinate[axisIndex], depth), depth);
                lines[axisIndex] = previous is not null &&
                                   CoordinatesSharePrefix(previous, coordinate, axisIndex + 1)
                    ? string.Empty
                    : label;
            }

            headers[coordinateIndex] = string.Join(Environment.NewLine, lines);
            previous = coordinate;
        }

        return headers;
    }

    private static bool CoordinatesSharePrefix(
        IReadOnlyList<int> left,
        IReadOnlyList<int> right,
        int prefixLength)
    {
        if (left.Count < prefixLength || right.Count < prefixLength)
        {
            return false;
        }

        for (var index = 0; index < prefixLength; index++)
        {
            if (left[index] != right[index])
            {
                return false;
            }
        }

        return true;
    }

    private bool TryBuildNestedTensorSection(object? value, out MatrixDisplaySection section)
    {
        section = new MatrixDisplaySection([], Array.Empty<IReadOnlyList<object?>>(), PreferNumericHeaders: true);

        if (value is Array array && array.Rank > 2)
        {
            var rows = BuildNestedTensorRows(array);

            if (rows.Count == 0)
            {
                return false;
            }

            section = new MatrixDisplaySection([], rows, PreferNumericHeaders: true);
            return true;
        }

        if (!TryGetSequenceItems(value, out var outerItems) || outerItems.Count == 0)
        {
            return false;
        }

        if (TryBuildNestedTensorGridRows(outerItems, out var gridRows))
        {
            section = new MatrixDisplaySection([], gridRows, PreferNumericHeaders: true);
            return true;
        }

        if (outerItems.All(CanRenderAsMatrixLikeValue))
        {
            var rows = outerItems
                .Select(item => (IReadOnlyList<object?>)[item])
                .ToArray();
            section = new MatrixDisplaySection([], rows, PreferNumericHeaders: true);
            return true;
        }

        return false;
    }

    private static IReadOnlyList<IReadOnlyList<object?>> BuildNestedTensorRows(Array array)
    {
        var outerAxisCount = array.Rank == 3 ? 1 : 2;
        var rowLength = array.GetLength(0);
        var rows = new List<IReadOnlyList<object?>>(rowLength);

        if (outerAxisCount == 1)
        {
            for (var rowIndex = 0; rowIndex < rowLength; rowIndex++)
            {
                rows.Add([BuildArraySliceValue(array, [rowIndex])]);
            }

            return rows;
        }

        var columnLength = array.GetLength(1);

        for (var rowIndex = 0; rowIndex < rowLength; rowIndex++)
        {
            var row = new object?[columnLength];

            for (var columnIndex = 0; columnIndex < columnLength; columnIndex++)
            {
                row[columnIndex] = BuildArraySliceValue(array, [rowIndex, columnIndex]);
            }

            rows.Add(row);
        }

        return rows;
    }

    private static object? BuildArraySliceValue(Array array, IReadOnlyList<int> fixedIndices)
    {
        var remainingRank = array.Rank - fixedIndices.Count;

        if (remainingRank <= 0)
        {
            return array.GetValue(fixedIndices.ToArray());
        }

        var lengths = Enumerable.Range(fixedIndices.Count, remainingRank)
            .Select(array.GetLength)
            .ToArray();
        return BuildArraySliceValueRecursive(array, fixedIndices, lengths, depth: 0);
    }

    private static object? BuildArraySliceValueRecursive(
        Array array,
        IReadOnlyList<int> fixedIndices,
        IReadOnlyList<int> remainingLengths,
        int depth)
    {
        if (depth == remainingLengths.Count)
        {
            var indices = new int[fixedIndices.Count];

            for (var index = 0; index < fixedIndices.Count; index++)
            {
                indices[index] = fixedIndices[index];
            }

            return array.GetValue(indices);
        }

        var count = remainingLengths[depth];
        var items = new object?[count];

        for (var index = 0; index < count; index++)
        {
            items[index] = BuildArraySliceValueRecursive(array, [.. fixedIndices, index], remainingLengths, depth + 1);
        }

        return items;
    }

    private bool TryBuildNestedTensorGridRows(
        IReadOnlyList<object?> outerItems,
        out IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        rows = Array.Empty<IReadOnlyList<object?>>();

        var builtRows = new List<IReadOnlyList<object?>>(outerItems.Count);
        var sawNestedCell = false;

        foreach (var outerItem in outerItems)
        {
            if (!TryGetSequenceItems(outerItem, out var innerItems) || innerItems.Count == 0)
            {
                return false;
            }

            if (!innerItems.All(CanRenderAsMatrixLikeValue))
            {
                return false;
            }

            sawNestedCell = true;
            builtRows.Add(innerItems);
        }

        if (!sawNestedCell)
        {
            return false;
        }

        rows = builtRows;
        return true;
    }

    private bool CanRenderAsMatrixLikeValue(object? value)
    {
        return value is not null && TryBuildMatrixSections(value, [], out _);
    }

    private bool TryBuildMatrixSections(
        object? value,
        IReadOnlyList<int> slicePath,
        out IReadOnlyList<MatrixDisplaySection> sections)
    {
        sections = Array.Empty<MatrixDisplaySection>();

        if (TryBuildRectangularMatrixSections(value, slicePath, out sections))
        {
            return sections.Count > 0;
        }

        if (TryBuildJaggedMatrixRows(value, out var rows))
        {
            sections = [new MatrixDisplaySection(slicePath.ToArray(), rows)];
            return true;
        }

        if (!TryGetSequenceItems(value, out var items) || items.Count == 0)
        {
            return false;
        }

        var nestedSections = new List<MatrixDisplaySection>();

        for (var index = 0; index < items.Count; index++)
        {
            if (!TryBuildMatrixSections(items[index], [.. slicePath, index], out var childSections))
            {
                return false;
            }

            nestedSections.AddRange(childSections);
        }

        if (nestedSections.Count == 0)
        {
            return false;
        }

        sections = nestedSections;
        return true;
    }

    private bool TryBuildRectangularMatrixSections(
        object? value,
        IReadOnlyList<int> slicePath,
        out IReadOnlyList<MatrixDisplaySection> sections)
    {
        sections = Array.Empty<MatrixDisplaySection>();

        if (value is not Array array || array.Rank < 2)
        {
            return false;
        }

        if (array.Rank == 2)
        {
            var rows = BuildRectangularMatrixRows(array, []);
            sections = rows.Count == 0
                ? Array.Empty<MatrixDisplaySection>()
                : [new MatrixDisplaySection(slicePath.ToArray(), rows)];
            return sections.Count > 0;
        }

        var leadingRank = array.Rank - 2;
        var lengths = Enumerable.Range(0, leadingRank)
            .Select(array.GetLength)
            .ToArray();
        var builtSections = new List<MatrixDisplaySection>();

        foreach (var leadingIndices in EnumerateIndexVectors(lengths))
        {
            var rows = BuildRectangularMatrixRows(array, leadingIndices);

            if (rows.Count == 0)
            {
                continue;
            }

            builtSections.Add(new MatrixDisplaySection([.. slicePath, .. leadingIndices], rows));
        }

        sections = builtSections;
        return sections.Count > 0;
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

        var title = ShouldRenderSingleRecordWithTitle(rowType)
            ? GetSingleRecordTitle(rowType)
            : null;
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

    private string RenderMatrixSections(
        IReadOnlyList<MatrixDisplaySection> sections,
        DisplayRenderOptions options)
    {
        if (sections.Count == 0)
        {
            return string.Empty;
        }

        if (sections.Count == 1 && sections[0].SlicePath.Count == 0)
        {
            return RenderMatrixSection(sections[0], options);
        }

        var renderedSections = sections
            .Select(section =>
            {
                var heading = TableTheme.RecordKey
                    .Apply($"[Slice {FormatMatrixSlicePath(section.SlicePath, options.MatrixLabelDepth)}]")
                    .ToAnsi();
                return $"{heading}{Environment.NewLine}{RenderMatrixSection(section, options)}";
            })
            .ToArray();

        return string.Join($"{Environment.NewLine}{Environment.NewLine}", renderedSections);
    }

    private string RenderMatrixSection(MatrixDisplaySection section, DisplayRenderOptions options)
    {
        var rows = section.Rows;
        var columnCount = rows.Max(row => row.Count);

        if (columnCount == 0)
        {
            return "[]";
        }

        var rawColumnHeaders = section.PreferNumericHeaders
            ? BuildIndexedMatrixColumnHeaders(columnCount, options.MatrixLabelDepth)
            : BuildMatrixColumnHeaders(rows, columnCount, options.MatrixLabelDepth);
        var columnHeaders = rawColumnHeaders
            .Select(header => StyleMatrixAxisLabel(header, options.MatrixLabelDepth))
            .ToArray();
        var columnMaxWidths = BuildMatrixColumnMaxWidths(rows, columnCount, options);
        var matrixRows = rows
            .Select((row, index) => new MatrixDisplayRow(StyleMatrixAxisLabel(FormatMatrixAxisLabel(index, options.MatrixLabelDepth), options.MatrixLabelDepth), row))
            .Cast<object>()
            .ToArray();

        var columns = new List<DisplayTableColumn>(columnCount + 1)
        {
            new(
                string.Empty,
                row => ((MatrixDisplayRow)row).IndexLabel,
                DisplayTableAlignment.Right,
                MinWidth: 1,
                MaxWidth: options.MaxTableCellWidth,
                Priority: 0,
                CanHide: false,
                UseHeaderTheme: false,
                UseIndexTheme: false),
        };

        for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
        {
            var capturedIndex = columnIndex;
            columns.Add(new DisplayTableColumn(
                columnHeaders[columnIndex],
                row => ((MatrixDisplayRow)row).TryGetCell(capturedIndex, out var value) ? value : null,
                Alignment: InferMatrixColumnAlignment(rows, capturedIndex),
                MinWidth: 1,
                MaxWidth: columnMaxWidths[capturedIndex],
                Priority: 10 + columnIndex,
                CanHide: false,
                UseHeaderTheme: false));
        }

        return RenderTable(matrixRows, columns, options, includeIndexColumn: false);
    }

    private static bool TryGetSequenceItems(object? value, out IReadOnlyList<object?> items)
    {
        items = Array.Empty<object?>();

        if (value is null ||
            value is string ||
            value is ShellTextLine ||
            value is IDictionary ||
            ShellRecordUtilities.IsRecordLike(value) ||
            value is not IEnumerable enumerable)
        {
            return false;
        }

        items = enumerable.Cast<object?>().ToArray();
        return true;
    }

    private static bool TryBuildJaggedMatrixRows(object? value, out IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        rows = Array.Empty<IReadOnlyList<object?>>();

        if (!TryGetSequenceItems(value, out var outerItems) || outerItems.Count == 0)
        {
            return false;
        }

        var builtRows = new List<IReadOnlyList<object?>>(outerItems.Count);
        var sawCell = false;

        foreach (var outerItem in outerItems)
        {
            if (!TryGetSequenceItems(outerItem, out var row))
            {
                return false;
            }

            if (row.Any(IsNestedTensorValue))
            {
                return false;
            }

            builtRows.Add(row);
            sawCell |= row.Count > 0;
        }

        if (!sawCell)
        {
            return false;
        }

        rows = builtRows;
        return true;
    }

    private static bool IsNestedTensorValue(object? value)
    {
        return TryGetSequenceItems(value, out _);
    }

    private static IReadOnlyList<IReadOnlyList<object?>> BuildRectangularMatrixRows(Array array, IReadOnlyList<int> leadingIndices)
    {
        var rowDimension = array.Rank - 2;
        var columnDimension = array.Rank - 1;
        var rowCount = array.GetLength(rowDimension);
        var columnCount = array.GetLength(columnDimension);
        var rowLowerBound = array.GetLowerBound(rowDimension);
        var columnLowerBound = array.GetLowerBound(columnDimension);
        var rows = new List<IReadOnlyList<object?>>(rowCount);

        for (var rowOffset = 0; rowOffset < rowCount; rowOffset++)
        {
            var row = new object?[columnCount];

            for (var columnOffset = 0; columnOffset < columnCount; columnOffset++)
            {
                var indices = new int[array.Rank];

                for (var index = 0; index < leadingIndices.Count; index++)
                {
                    indices[index] = leadingIndices[index];
                }

                indices[rowDimension] = rowLowerBound + rowOffset;
                indices[columnDimension] = columnLowerBound + columnOffset;
                row[columnOffset] = array.GetValue(indices);
            }

            rows.Add(row);
        }

        return rows;
    }

    private static IEnumerable<int[]> EnumerateIndexVectors(IReadOnlyList<int> lengths)
    {
        if (lengths.Count == 0)
        {
            yield return [];
            yield break;
        }

        var indices = new int[lengths.Count];

        while (true)
        {
            yield return (int[])indices.Clone();

            var position = indices.Length - 1;

            while (position >= 0)
            {
                indices[position]++;

                if (indices[position] < lengths[position])
                {
                    break;
                }

                indices[position] = 0;
                position--;
            }

            if (position < 0)
            {
                yield break;
            }
        }
    }

    private static string FormatMatrixSlicePath(IReadOnlyList<int> indices, int startDepth)
    {
        if (indices.Count == 0)
        {
            return FormatMatrixAxisLabel(0, startDepth);
        }

        return string.Join(
            ", ",
            indices.Select((index, depth) => FormatMatrixAxisLabel(index, startDepth + depth)));
    }

    private static string[] BuildMatrixColumnHeaders(
        IReadOnlyList<IReadOnlyList<object?>> rows,
        int columnCount,
        int labelDepth)
    {
        var stableHeaders = new string[columnCount];
        var seenHeaders = new HashSet<string>(StringComparer.Ordinal);
        var useTypeHeaders = true;

        for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
        {
            Type? stableType = null;

            foreach (var row in rows)
            {
                if (columnIndex >= row.Count || row[columnIndex] is null)
                {
                    continue;
                }

                var cellType = row[columnIndex]!.GetType();

                if (stableType is null)
                {
                    stableType = cellType;
                    continue;
                }

                if (stableType != cellType)
                {
                    useTypeHeaders = false;
                    break;
                }
            }

            if (!useTypeHeaders || stableType is null)
            {
                useTypeHeaders = false;
                break;
            }

            stableHeaders[columnIndex] = ObjectFormatter.GetTypeName(stableType);

            if (!seenHeaders.Add(stableHeaders[columnIndex]))
            {
                useTypeHeaders = false;
                break;
            }
        }

        if (useTypeHeaders &&
            seenHeaders.Count == columnCount &&
            seenHeaders.Count > 1)
        {
            return stableHeaders;
        }

        return Enumerable.Range(0, columnCount)
            .Select(index => FormatMatrixAxisLabel(index, labelDepth))
            .ToArray();
    }

    private static string[] BuildIndexedMatrixColumnHeaders(int columnCount, int labelDepth)
    {
        return Enumerable.Range(0, columnCount)
            .Select(index => FormatMatrixAxisLabel(index, labelDepth))
            .ToArray();
    }

    private static string FormatMatrixAxisLabel(int index, int depth)
    {
        return (depth % 5) switch
        {
            0 => index.ToString(CultureInfo.InvariantCulture),
            1 => FormatAlphabeticLabel(index, uppercase: true),
            2 => FormatRomanLabel(index + 1, uppercase: true),
            3 => FormatAlphabeticLabel(index, uppercase: false),
            _ => FormatRomanLabel(index + 1, uppercase: false),
        };
    }

    private string StyleMatrixAxisLabel(string label, int depth)
    {
        if (string.IsNullOrEmpty(label))
        {
            return label;
        }

        return GetMatrixDepthTheme(depth)
            .Apply(label)
            .ToAnsi();
    }

    private ToshTextStyleConfig GetMatrixDepthTheme(int depth)
    {
        return Math.Abs(depth % 5) switch
        {
            0 => TableTheme.MatrixDepth0,
            1 => TableTheme.MatrixDepth1,
            2 => TableTheme.MatrixDepth2,
            3 => TableTheme.MatrixDepth3,
            _ => TableTheme.MatrixDepth4,
        };
    }

    private static string FormatAlphabeticLabel(int index, bool uppercase)
    {
        const string upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        const string lower = "abcdefghijklmnopqrstuvwxyz";
        var alphabet = uppercase ? upper : lower;
        var builder = new StringBuilder();
        var value = index;

        do
        {
            builder.Insert(0, alphabet[value % 26]);
            value = (value / 26) - 1;
        }
        while (value >= 0);

        return builder.ToString();
    }

    private static string FormatRomanLabel(int value, bool uppercase)
    {
        if (value <= 0 || value >= 4000)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        var numerals = new (int Value, string Symbol)[]
        {
            (1000, "M"),
            (900, "CM"),
            (500, "D"),
            (400, "CD"),
            (100, "C"),
            (90, "XC"),
            (50, "L"),
            (40, "XL"),
            (10, "X"),
            (9, "IX"),
            (5, "V"),
            (4, "IV"),
            (1, "I"),
        };
        var builder = new StringBuilder();
        var remaining = value;

        foreach (var numeral in numerals)
        {
            while (remaining >= numeral.Value)
            {
                builder.Append(numeral.Symbol);
                remaining -= numeral.Value;
            }
        }

        return uppercase
            ? builder.ToString()
            : builder.ToString().ToLowerInvariant();
    }

    private static int[] BuildMatrixColumnMaxWidths(
        IReadOnlyList<IReadOnlyList<object?>> rows,
        int columnCount,
        DisplayRenderOptions options)
    {
        var widths = new int[columnCount];
        var structuredColumnBudget = GetStructuredMatrixColumnWidthBudget(options);

        for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
        {
            var hasStructuredCell = rows.Any(row =>
                columnIndex < row.Count &&
                row[columnIndex] is string text &&
                text.Contains('\n'));

            widths[columnIndex] = hasStructuredCell
                ? structuredColumnBudget
                : options.MaxTableCellWidth;
        }

        return widths;
    }

    private static int GetStructuredMatrixColumnWidthBudget(DisplayRenderOptions options)
    {
        if (options.MaxWidth is not int maxWidth || maxWidth <= 0)
        {
            return Math.Max(options.MaxTableCellWidth, 72);
        }

        return Math.Max(options.MaxTableCellWidth, maxWidth - 8);
    }

    private static DisplayTableAlignment InferMatrixColumnAlignment(IReadOnlyList<IReadOnlyList<object?>> rows, int columnIndex)
    {
        foreach (var row in rows)
        {
            if (columnIndex < row.Count && row[columnIndex] is not null)
            {
                return IsRightAlignedType(row[columnIndex]!.GetType())
                    ? DisplayTableAlignment.Right
                    : DisplayTableAlignment.Left;
            }
        }

        return DisplayTableAlignment.Left;
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

    private IReadOnlyList<DisplayTableColumn> BuildRecordLikeColumns(IReadOnlyList<object> rows, bool allowStructuredValues = false)
    {
        if (rows.Count == 0 || !ShellRecordUtilities.TryGetFields(rows[0], out var fields))
        {
            return Array.Empty<DisplayTableColumn>();
        }

        return fields
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

        if (rows.Count == 0 || rows.Any(row => !ShellRecordUtilities.IsRecordLike(row)))
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

        columns = BuildGenericColumns(rowType, allowStructuredValues: true);
        columns = ApplyColumnPreferences(rowType, row, columns, allowStructuredValues: true, options);
        return columns.Count > 0;
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
        var splitCells = cells.Select(SplitLines).ToArray();
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

    private static bool TryFormatPrettyScalar(
        object value,
        out string typeName,
        out string valueText,
        out DisplayTableAlignment valueAlignment)
    {
        typeName = string.Empty;
        valueText = string.Empty;
        valueAlignment = DisplayTableAlignment.Left;

        if (value is ShellTextLine or StyledText or Type or Enum)
        {
            return false;
        }

        var runtimeType = value.GetType();

        if (value is string text)
        {
            typeName = runtimeType.Name;
            valueText = text;
            return true;
        }

        if (value is char character)
        {
            typeName = runtimeType.Name;
            valueText = character.ToString();
            return true;
        }

        if (value is bool boolean)
        {
            typeName = runtimeType.Name;
            valueText = boolean.ToString().ToLowerInvariant();
            return true;
        }

        if (value is DateTime dateTime)
        {
            typeName = runtimeType.Name;
            valueText = dateTime.ToString("O", CultureInfo.InvariantCulture);
            return true;
        }

        if (value is DateTimeOffset dateTimeOffset)
        {
            typeName = runtimeType.Name;
            valueText = dateTimeOffset.ToString("O", CultureInfo.InvariantCulture);
            return true;
        }

        if (value is Uri uri)
        {
            typeName = runtimeType.Name;
            valueText = uri.ToString();
            return true;
        }

        if (value is ToshVector vector)
        {
            typeName = vector.ShellTypeName;
            valueText = vector.ToString(null, CultureInfo.InvariantCulture);
            return true;
        }

        if (value is Complex complex)
        {
            typeName = ComplexShellType.Instance.ShellTypeName;
            valueText = ComplexShellType.FormatCompact(complex);
            return true;
        }

        if (value is Guid guid)
        {
            typeName = runtimeType.Name;
            valueText = guid.ToString();
            return true;
        }

        if (value is TimeSpan timeSpan)
        {
            typeName = runtimeType.Name;
            valueText = timeSpan.ToString("c", CultureInfo.InvariantCulture);
            return true;
        }

        if (value is Units.Quantity quantity)
        {
            typeName = quantity.CategoryName;
            valueText = quantity.ToString();
            valueAlignment = DisplayTableAlignment.Right;
            return true;
        }

        if (value is decimal decimalValue)
        {
            typeName = runtimeType.Name;
            valueText = decimalValue.ToString(CultureInfo.InvariantCulture);
            valueAlignment = DisplayTableAlignment.Right;
            return true;
        }

        if (value is BigInteger bigInteger)
        {
            typeName = runtimeType.Name;
            valueText = bigInteger.ToString("N0", CultureInfo.InvariantCulture);
            valueAlignment = DisplayTableAlignment.Right;
            return true;
        }

        if (IsIntegralScalarType(runtimeType) && value is IConvertible convertible)
        {
            typeName = runtimeType.Name;
            valueText = Convert.ToDecimal(convertible, CultureInfo.InvariantCulture).ToString("N0", CultureInfo.InvariantCulture);
            valueAlignment = DisplayTableAlignment.Right;
            return true;
        }

        if ((runtimeType == typeof(float) || runtimeType == typeof(double)) &&
            value is IFormattable floatingPoint)
        {
            typeName = runtimeType.Name;
            valueText = floatingPoint.ToString(null, CultureInfo.InvariantCulture) ?? value.ToString() ?? string.Empty;
            valueAlignment = DisplayTableAlignment.Right;
            return true;
        }

        return false;
    }

    private bool TryFormatProfileBackedPrettyScalar(
        object value,
        DisplayRenderOptions options,
        out string typeName,
        out string valueText,
        out DisplayTableAlignment valueAlignment)
    {
        typeName = string.Empty;
        valueText = string.Empty;
        valueAlignment = DisplayTableAlignment.Left;

        if (value is ShellTextLine or StyledText or ICommandResult or Type)
        {
            return false;
        }

        var runtimeType = value.GetType();

        if (!IsProfileBackedPrettyScalarType(runtimeType))
        {
            return false;
        }

        var profile = _profiles.Resolve(runtimeType);

        if (profile is null || profile.TargetType != runtimeType)
        {
            return false;
        }

        var tableContext = new DisplayTableContext(runtimeType, [value], options);

        if (profile.TryBuildTable(tableContext, out _))
        {
            return false;
        }

        var renderContext = new DisplayValueContext(
            value,
            DisplaySurface.Root,
            options.Style,
            RenderOptions: options,
            FormattingOptions: new ObjectFormattingOptions(options.Style));

        if (!profile.TryRender(renderContext, out valueText) || string.IsNullOrWhiteSpace(valueText))
        {
            return false;
        }

        typeName = ObjectFormatter.GetTypeName(runtimeType);
        valueAlignment = IsRightAlignedType(runtimeType) ? DisplayTableAlignment.Right : DisplayTableAlignment.Left;
        return true;
    }

    private static bool IsProfileBackedPrettyScalarType(Type type)
    {
        var effectiveType = Nullable.GetUnderlyingType(type) ?? type;

        return effectiveType == typeof(DateTime) ||
               effectiveType == typeof(DateOnly) ||
               effectiveType == typeof(DateTimeOffset) ||
               effectiveType == typeof(TimeOnly) ||
               effectiveType == typeof(TimeSpan) ||
               effectiveType == typeof(StorageSize) ||
               effectiveType == typeof(TemporalAmount) ||
               effectiveType == typeof(Complex) ||
               typeof(Units.Quantity).IsAssignableFrom(effectiveType) ||
               effectiveType == typeof(Uri) ||
               effectiveType == typeof(IPAddress) ||
               effectiveType == typeof(UnixFileMode) ||
               effectiveType == typeof(FileAttributes) ||
               effectiveType == typeof(FileSystemPrincipalInfo) ||
               effectiveType == typeof(FileSystemEntryType) ||
               effectiveType == typeof(ShellJobStatus) ||
               effectiveType == typeof(HelpSubjectKind) ||
               effectiveType == typeof(CommandResolutionKind);
    }

    private static bool IsIntegralScalarType(Type type)
    {
        return type == typeof(byte) ||
               type == typeof(sbyte) ||
               type == typeof(short) ||
               type == typeof(ushort) ||
               type == typeof(int) ||
               type == typeof(uint) ||
               type == typeof(long) ||
               type == typeof(ulong) ||
               type == typeof(BigInteger) ||
               type == typeof(nint) ||
               type == typeof(nuint);
    }

    private static bool IsMixedNumericScalarType(Type type)
    {
        var effectiveType = Nullable.GetUnderlyingType(type) ?? type;
        return IsIntegralScalarType(effectiveType) ||
               effectiveType == typeof(decimal) ||
               effectiveType == typeof(float) ||
               effectiveType == typeof(double);
    }

    private static bool ShouldPreferValueList(IReadOnlyList<object?> values)
    {
        if (values.Count == 0)
        {
            return false;
        }

        foreach (var value in values)
        {
            if (value is null || !ObjectFormatter.TryFormatSimple(value, isRoot: true, out _))
            {
                return false;
            }
        }

        return true;
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

        var plainText = StyledText.StripAnsi(value);
        return width == 1 ? plainText[..1] : $"{plainText[..Math.Min(width - 1, plainText.Length)]}…";
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

    private static IReadOnlyList<string> SplitLines(string text)
    {
        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');
    }

    private static int GetCellDisplayWidth(string value)
    {
        return SplitLines(value).Max(StyledText.GetVisibleLength);
    }

    private static int GetRenderedRowHeight(IReadOnlyList<string> cells)
    {
        if (cells.Count == 0)
        {
            return 0;
        }

        return cells.Max(cell => SplitLines(cell).Count);
    }

    private static string PadCellLeft(string value, int width)
    {
        var padding = Math.Max(0, width - StyledText.GetVisibleLength(value));
        return padding == 0 ? value : $"{new string(' ', padding)}{value}";
    }

    private static string PadCellRight(string value, int width)
    {
        var padding = Math.Max(0, width - StyledText.GetVisibleLength(value));
        return padding == 0 ? value : $"{value}{new string(' ', padding)}";
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

    private sealed class MixedTypeGroup(string identity, string displayName, bool isNullGroup)
    {
        public string Identity { get; } = identity;

        public string DisplayName { get; } = displayName;

        public bool IsNullGroup { get; } = isNullGroup;

        public List<object?> Values { get; } = [];
    }

    private readonly record struct MixedTypeGroupKey(string Identity, string DisplayName, bool IsNullGroup);

    private sealed record MatrixDisplaySection(IReadOnlyList<int> SlicePath, IReadOnlyList<IReadOnlyList<object?>> Rows, bool PreferNumericHeaders = false);

    private sealed record MatrixDisplayRow(string IndexLabel, IReadOnlyList<object?> Cells)
    {
        public bool TryGetCell(int index, out object? value)
        {
            if (index >= 0 && index < Cells.Count)
            {
                value = Cells[index];
                return true;
            }

            value = null;
            return false;
        }
    }

    private sealed record FlattenedTensorCell(IReadOnlyList<int> Indices, object? Value);

    private sealed record FlattenedTensorRow(IReadOnlyList<string> AxisLabels, IReadOnlyList<object?> Values)
    {
        public bool TryGetAxisLabel(int index, out string value)
        {
            if (index >= 0 && index < AxisLabels.Count)
            {
                value = AxisLabels[index];
                return true;
            }

            value = string.Empty;
            return false;
        }
    }

    private sealed record RecordRow(string Name, IReadOnlyList<string> ValueLines, DisplayTableAlignment Alignment);
}
