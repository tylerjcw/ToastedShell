namespace Tosh.Core;

internal static class AggregationUtilities
{
    internal enum SummaryOperationKind
    {
        Sum,
        Average,
        Min,
        Max,
        Count,
    }

    internal sealed class SummaryTarget
    {
        public SummaryTarget(string column, string? memberPath, IReadOnlyCollection<SummaryOperationKind> operations)
        {
            Column = column;
            MemberPath = memberPath;
            Operations = new HashSet<SummaryOperationKind>(operations);
        }

        public string Column { get; }

        public string? MemberPath { get; }

        public HashSet<SummaryOperationKind> Operations { get; }
    }

    public static async Task<IReadOnlyList<object?>> CollectValuesAsync(CommandContext context)
    {
        string? memberPath = null;

        if (context.Arguments.Count > 1)
        {
            throw new InvalidOperationException("This aggregation accepts at most one member path.");
        }

        if (context.Arguments.Count == 1)
        {
            memberPath = NormalizeMemberPath(context.Arguments[0]?.ToString());
        }

        var values = new List<object?>();

        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            var value = memberPath is null ? item : context.Runtime.ObjectAccessor.GetValue(item, memberPath);

            if (value is not null)
            {
                values.Add(value);
            }
        }

        return values;
    }

    // Ops applicable per value type — determines what gets included in auto mode.
    public static IReadOnlyCollection<SummaryOperationKind> InferApplicableOps(IReadOnlyList<object?> values)
    {
        var nonNull = values.Where(v => v is not null).ToArray();

        if (nonNull.Length == 0)
        {
            return [SummaryOperationKind.Count];
        }

        var ops = new HashSet<SummaryOperationKind> { SummaryOperationKind.Count };

        if (TryGetNumbers(nonNull, out _) || TryGetStorageSizes(nonNull, out _) || TryGetTimeSpans(nonNull, out _))
        {
            ops.Add(SummaryOperationKind.Sum);
            ops.Add(SummaryOperationKind.Average);
        }

        // min/max: numeric, storage, timespan, string, DateTime, DateTimeOffset
        if (TryGetNumbers(nonNull, out _) || TryGetStorageSizes(nonNull, out _) || TryGetTimeSpans(nonNull, out _) ||
            nonNull.All(v => v is string) || nonNull.All(v => v is DateTime) || nonNull.All(v => v is DateTimeOffset))
        {
            ops.Add(SummaryOperationKind.Min);
            ops.Add(SummaryOperationKind.Max);
        }

        return ops;
    }

    public static async Task<IReadOnlyList<ColumnSummary>> SummarizeAutoAsync(
        CommandContext context,
        string? singleColumnFilter)
    {
        var rows = await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);

        if (rows.Count == 0)
        {
            return Array.Empty<ColumnSummary>();
        }

        var firstNonNull = rows.FirstOrDefault(r => r is not null);
        var normalizedFilter = NormalizeMemberPath(singleColumnFilter);

        if (normalizedFilter is not null)
        {
            return await SummarizeSingleAutoColumnAsync(context, rows, firstNonNull, normalizedFilter);
        }

        // Scalar-like input with no requested member path.
        if (firstNonNull is null || IsScalarSummaryValue(firstNonNull))
        {
            var values = rows.Where(v => v is not null).ToList();
            var ops = InferApplicableOps(values!);
            var scalarTarget = new SummaryTarget("Value", null, ops);
            return await SummarizeFromBufferAsync(rows, [scalarTarget], context);
        }

        var columnNames = DiscoverColumnNames(firstNonNull)
            .ToArray();

        if (columnNames.Length == 0)
        {
            var values = rows.Where(v => v is not null).ToList();
            var ops = InferApplicableOps(values!);
            var scalarTarget = new SummaryTarget("Value", null, ops);
            return await SummarizeFromBufferAsync(rows, [scalarTarget], context);
        }

        // Collect per-column values to infer applicable ops
        var columnValues = columnNames.ToDictionary(
            col => col,
            col => rows
                .Select(row => row is not null ? TryGetColumnValue(context, row, col) : null)
                .Where(v => v is not null)
                .ToList(),
            StringComparer.OrdinalIgnoreCase);

        var targets = columnNames
            .Select(col => new SummaryTarget(col, col, InferApplicableOps(columnValues[col]!)))
            .ToArray();

        return await SummarizeFromBufferAsync(rows, targets, context);
    }

    public static string? NormalizeMemberPath(string? memberPath)
    {
        if (string.IsNullOrWhiteSpace(memberPath))
        {
            return null;
        }

        var normalized = memberPath.Trim();

        if (string.Equals(normalized, "_", StringComparison.Ordinal) ||
            string.Equals(normalized, "$_", StringComparison.Ordinal))
        {
            return null;
        }

        if (normalized.StartsWith("$_.", StringComparison.Ordinal))
        {
            normalized = normalized[3..];
        }
        else if (normalized.StartsWith("_.", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : normalized;
    }

    private static async Task<IReadOnlyList<ColumnSummary>> SummarizeSingleAutoColumnAsync(
        CommandContext context,
        IReadOnlyList<object?> rows,
        object? firstNonNull,
        string normalizedFilter)
    {
        if (string.Equals(normalizedFilter, "Value", StringComparison.OrdinalIgnoreCase))
        {
            var scalarValues = rows.Where(v => v is not null).ToList();
            var ops = InferApplicableOps(scalarValues!);
            return await SummarizeFromBufferAsync(rows, [new SummaryTarget("Value", null, ops)], context);
        }

        if (firstNonNull is not null &&
            !TryGetColumnValue(context, firstNonNull, normalizedFilter, out _))
        {
            throw new InvalidOperationException($"Column '{normalizedFilter}' was not found in the input.");
        }

        var values = rows
            .Select(row => row is not null && TryGetColumnValue(context, row, normalizedFilter, out var value) ? value : null)
            .Where(value => value is not null)
            .ToList();

        var target = new SummaryTarget(normalizedFilter, normalizedFilter, InferApplicableOps(values!));
        return await SummarizeFromBufferAsync(rows, [target], context);
    }

    private static object? TryGetColumnValue(CommandContext context, object row, string column)
    {
        return TryGetColumnValue(context, row, column, out var value)
            ? value
            : null;
    }

    private static bool TryGetColumnValue(CommandContext context, object row, string column, out object? value)
    {
        if (ShellRecordUtilities.TryGetValue(row, column, out value))
        {
            return true;
        }

        try
        {
            value = context.Runtime.ObjectAccessor.GetValue(row, column);
            return true;
        }
        catch (InvalidOperationException)
        {
            value = null;
            return false;
        }
    }

    private static IReadOnlyList<string> DiscoverColumnNames(object row)
    {
        if (ShellRecordUtilities.TryGetFields(row, out var fields))
        {
            return fields
                .Select(field => field.Key)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rowType = row.GetType();

        foreach (var property in rowType.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length == 0)
            {
                names.Add(property.Name);
            }
        }

        foreach (var field in rowType.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            names.Add(field.Name);
        }

        foreach (var name in ObjectMemberAdapter.GetMemberNames(rowType))
        {
            names.Add(name);
        }

        return names
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsScalarSummaryValue(object value)
    {
        if (ShellRecordUtilities.IsRecordLike(value))
        {
            return false;
        }

        if (value is string or char or bool or Guid or Uri or System.Net.IPAddress or StorageSize or TimeSpan or DateTime or DateTimeOffset)
        {
            return true;
        }

        var valueType = value.GetType();

        if (valueType.IsPrimitive || valueType.IsEnum || value is decimal)
        {
            return true;
        }

        return value is System.Collections.IEnumerable;
    }

    private static Task<IReadOnlyList<ColumnSummary>> SummarizeFromBufferAsync(
        IReadOnlyList<object?> rows,
        IReadOnlyList<SummaryTarget> targets,
        CommandContext context)
    {
        var accumulators = targets
            .Select(target => new SummaryAccumulator(target.Column, target.MemberPath, target.Operations))
            .ToArray();

        foreach (var item in rows)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            foreach (var accumulator in accumulators)
            {
                accumulator.RowCount++;

                var value = accumulator.MemberPath is null
                    ? item
                    : item is null ? null : TryGetColumnValue(context, item, accumulator.MemberPath, out var memberValue) ? memberValue : null;

                if (value is not null)
                {
                    accumulator.Values.Add(value);
                }
            }
        }

        IReadOnlyList<ColumnSummary> results = accumulators
            .Select(BuildColumnSummary)
            .ToArray();

        return Task.FromResult(results);
    }

    public static async Task<IReadOnlyList<ColumnSummary>> SummarizeAsync(
        CommandContext context,
        IReadOnlyList<SummaryTarget> targets)
    {
        var accumulators = targets
            .Select(target => new SummaryAccumulator(target.Column, target.MemberPath, target.Operations))
            .ToArray();

        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            foreach (var accumulator in accumulators)
            {
                accumulator.RowCount++;

                object? value;

                if (accumulator.MemberPath is null)
                {
                    value = item;
                }
                else
                {
                    value = item is null
                        ? null
                        : context.Runtime.ObjectAccessor.GetValue(item, accumulator.MemberPath);
                }

                if (value is null)
                {
                    continue;
                }

                accumulator.Values.Add(value);
            }
        }

        return accumulators
            .Select(BuildColumnSummary)
            .ToArray();
    }

    public static object Sum(IReadOnlyList<object?> values)
    {
        if (TryGetStorageSizes(values, out var sizes))
        {
            checked
            {
                return StorageSize.FromBytes(sizes.Aggregate(0L, (total, size) => total + size.Bytes));
            }
        }

        if (TryGetTimeSpans(values, out var spans))
        {
            checked
            {
                return new TimeSpan(spans.Aggregate(0L, (total, span) => total + span.Ticks));
            }
        }

        if (TryGetNumbers(values, out var numbers))
        {
            if (numbers.Integral)
            {
                checked
                {
                    return (long)numbers.Values.Sum();
                }
            }

            return numbers.Values.Sum();
        }

        throw new InvalidOperationException("sum expects numeric, storage size, or timespan values.");
    }

    public static object Average(IReadOnlyList<object?> values)
    {
        if (TryGetStorageSizes(values, out var sizes))
        {
            return StorageSize.FromBytes((long)Math.Round(sizes.Average(size => size.Bytes), MidpointRounding.AwayFromZero));
        }

        if (TryGetTimeSpans(values, out var spans))
        {
            return new TimeSpan((long)Math.Round(spans.Average(span => span.Ticks), MidpointRounding.AwayFromZero));
        }

        if (TryGetNumbers(values, out var numbers))
        {
            var average = numbers.Values.Average();
            return numbers.Integral
                ? (object)(long)Math.Round(average, MidpointRounding.AwayFromZero)
                : average;
        }

        throw new InvalidOperationException("average expects numeric, storage size, or timespan values.");
    }

    public static object? Min(IReadOnlyList<object?> values)
    {
        return Extremum(values, max: false);
    }

    public static object? Max(IReadOnlyList<object?> values)
    {
        return Extremum(values, max: true);
    }

    private static object? Extremum(IReadOnlyList<object?> values, bool max)
    {
        if (TryGetStorageSizes(values, out var sizes))
        {
            return max ? sizes.Max() : sizes.Min();
        }

        if (TryGetTimeSpans(values, out var spans))
        {
            return max ? spans.Max() : spans.Min();
        }

        if (TryGetNumbers(values, out var numbers))
        {
            var result = max ? numbers.Values.Max() : numbers.Values.Min();
            return numbers.Integral ? (object)(long)result : result;
        }

        if (values.All(value => value is string))
        {
            return max ? values.Cast<string>().Max(StringComparer.Ordinal) : values.Cast<string>().Min(StringComparer.Ordinal);
        }

        if (values.All(value => value is DateTime))
        {
            return max ? values.Cast<DateTime>().Max() : values.Cast<DateTime>().Min();
        }

        if (values.All(value => value is DateTimeOffset))
        {
            return max ? values.Cast<DateTimeOffset>().Max() : values.Cast<DateTimeOffset>().Min();
        }

        throw new InvalidOperationException("min/max expects comparable values.");
    }

    private static bool TryGetStorageSizes(IReadOnlyList<object?> values, out IReadOnlyList<StorageSize> sizes)
    {
        if (values.All(value => value is StorageSize))
        {
            sizes = values.Cast<StorageSize>().ToArray();
            return true;
        }

        sizes = Array.Empty<StorageSize>();
        return false;
    }

    private static bool TryGetTimeSpans(IReadOnlyList<object?> values, out IReadOnlyList<TimeSpan> spans)
    {
        if (values.All(value => value is TimeSpan))
        {
            spans = values.Cast<TimeSpan>().ToArray();
            return true;
        }

        spans = Array.Empty<TimeSpan>();
        return false;
    }

    private static bool TryGetNumbers(IReadOnlyList<object?> values, out (double[] Values, bool Integral) numbers)
    {
        var converted = new double[values.Count];
        var integral = true;

        for (var index = 0; index < values.Count; index++)
        {
            if (values[index] is null ||
                !TypeConversion.TryConvert(values[index], typeof(double), out var convertedValue) ||
                convertedValue is not double numeric)
            {
                numbers = default;
                return false;
            }

            if (values[index] is float or double or decimal)
            {
                integral = false;
            }

            converted[index] = numeric;
        }

        numbers = (converted, integral);
        return true;
    }

    private static ColumnSummary BuildColumnSummary(SummaryAccumulator accumulator)
    {
        var summary = new ColumnSummary
        {
            Column = accumulator.Column,
            RowCount = accumulator.RowCount,
            ValueCount = accumulator.Values.Count,
        };

        if (accumulator.Operations.Contains(SummaryOperationKind.Count))
        {
            summary = summary with
            {
                Count = accumulator.MemberPath is null
                    ? accumulator.RowCount
                    : accumulator.Values.Count,
            };
        }

        if (accumulator.Values.Count == 0)
        {
            return summary;
        }

        if (accumulator.Operations.Contains(SummaryOperationKind.Sum))
        {
            summary = summary with { Sum = Sum(accumulator.Values) };
        }

        if (accumulator.Operations.Contains(SummaryOperationKind.Average))
        {
            summary = summary with { Average = Average(accumulator.Values) };
        }

        if (accumulator.Operations.Contains(SummaryOperationKind.Min))
        {
            summary = summary with { Min = Min(accumulator.Values) };
        }

        if (accumulator.Operations.Contains(SummaryOperationKind.Max))
        {
            summary = summary with { Max = Max(accumulator.Values) };
        }

        return summary;
    }

    private sealed class SummaryAccumulator
    {
        public SummaryAccumulator(
            string column,
            string? memberPath,
            IReadOnlyCollection<SummaryOperationKind> operations)
        {
            Column = column;
            MemberPath = memberPath;
            Operations = operations;
        }

        public string Column { get; }

        public string? MemberPath { get; }

        public IReadOnlyCollection<SummaryOperationKind> Operations { get; }

        public int RowCount { get; set; }

        public List<object?> Values { get; } = [];
    }
}
