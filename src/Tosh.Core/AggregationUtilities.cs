namespace Tosh.Core;

internal static class AggregationUtilities
{
    public static async Task<IReadOnlyList<object?>> CollectValuesAsync(CommandContext context)
    {
        string? memberPath = null;

        if (context.Arguments.Count > 1)
        {
            throw new InvalidOperationException("This aggregation accepts at most one member path.");
        }

        if (context.Arguments.Count == 1)
        {
            memberPath = context.Arguments[0]?.ToString();
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
}
