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


}
