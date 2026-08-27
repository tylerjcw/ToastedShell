using System.Globalization;
using System.Text;

namespace Tosh.Runtime;

public readonly record struct TemporalAmount(long Months, TimeSpan Duration) : IFormattable
{
    private const long MonthsPerYear = 12;
    private const long MonthsPerDecade = 120;
    private const long MonthsPerCentury = 1_200;
    private const long MonthsPerMillennium = 12_000;
    private const long MonthsPerMillionYears = 12_000_000;
    private const long MonthsPerBillionYears = 12_000_000_000;
    private const long MonthsPerTrillionYears = 12_000_000_000_000;

    public static TemporalAmount Zero => new(0, TimeSpan.Zero);

    public bool HasCalendarUnits => Months != 0;

    public bool IsPureTimeSpan => Months == 0;

    public static TemporalAmount FromTimeSpan(TimeSpan duration) => new(0, duration);

    public bool TryAsTimeSpan(out TimeSpan value)
    {
        if (Months == 0)
        {
            value = Duration;
            return true;
        }

        value = default;
        return false;
    }

    public TemporalAmount Add(TemporalAmount other)
    {
        return new TemporalAmount(checked(Months + other.Months), Duration + other.Duration);
    }

    public TemporalAmount Subtract(TemporalAmount other)
    {
        return new TemporalAmount(checked(Months - other.Months), Duration - other.Duration);
    }

    public DateTimeOffset AddTo(DateTimeOffset value)
    {
        return ApplyTo(value, add: true);
    }

    public DateTimeOffset SubtractFrom(DateTimeOffset value)
    {
        return ApplyTo(value, add: false);
    }

    public DateTime AddTo(DateTime value)
    {
        return ApplyTo(value, add: true);
    }

    public DateTime SubtractFrom(DateTime value)
    {
        return ApplyTo(value, add: false);
    }

    public override string ToString()
    {
        return ToString(null, CultureInfo.InvariantCulture);
    }

    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        if (Months == 0 && Duration == TimeSpan.Zero)
        {
            return "0s";
        }

        var parts = new List<string>();

        if (Months != 0)
        {
            parts.Add(FormatMonths(Months));
        }

        if (Duration != TimeSpan.Zero)
        {
            parts.Add(FormatDuration(Duration));
        }

        return string.Join(" ", parts);
    }

    private DateTimeOffset ApplyTo(DateTimeOffset value, bool add)
    {
        var result = value;

        if (Months != 0)
        {
            result = AddMonths(result, add ? Months : -Months);
        }

        if (Duration != TimeSpan.Zero)
        {
            result = add ? result.Add(Duration) : result.Subtract(Duration);
        }

        return result;
    }

    private DateTime ApplyTo(DateTime value, bool add)
    {
        var result = value;

        if (Months != 0)
        {
            result = AddMonths(result, add ? Months : -Months);
        }

        if (Duration != TimeSpan.Zero)
        {
            result = add ? result.Add(Duration) : result.Subtract(Duration);
        }

        return result;
    }

    private static DateTimeOffset AddMonths(DateTimeOffset value, long months)
    {
        var remaining = months;
        var result = value;

        while (remaining != 0)
        {
            var chunk = remaining > int.MaxValue
                ? int.MaxValue
                : remaining < int.MinValue
                    ? int.MinValue
                    : (int)remaining;

            result = result.AddMonths(chunk);
            remaining -= chunk;
        }

        return result;
    }

    private static DateTime AddMonths(DateTime value, long months)
    {
        var remaining = months;
        var result = value;

        while (remaining != 0)
        {
            var chunk = remaining > int.MaxValue
                ? int.MaxValue
                : remaining < int.MinValue
                    ? int.MinValue
                    : (int)remaining;

            result = result.AddMonths(chunk);
            remaining -= chunk;
        }

        return result;
    }

    private static string FormatMonths(long months)
    {
        var builder = new StringBuilder();
        var remaining = Math.Abs(months);

        AppendWholeUnit(builder, ref remaining, MonthsPerTrillionYears, "Ta");
        AppendWholeUnit(builder, ref remaining, MonthsPerBillionYears, "Ga");
        AppendWholeUnit(builder, ref remaining, MonthsPerMillionYears, "Ma");
        AppendWholeUnit(builder, ref remaining, MonthsPerMillennium, "ka");
        AppendWholeUnit(builder, ref remaining, MonthsPerCentury, "c");
        AppendWholeUnit(builder, ref remaining, MonthsPerDecade, "da");
        AppendWholeUnit(builder, ref remaining, MonthsPerYear, "y");
        AppendWholeUnit(builder, ref remaining, 1, "mo");

        if (builder.Length == 0)
        {
            builder.Append("0mo");
        }

        if (months < 0)
        {
            builder.Insert(0, '-');
        }

        return builder.ToString();
    }

    private static string FormatDuration(TimeSpan duration)
    {
        var ticks = Math.Abs(duration.Ticks);
        var builder = new StringBuilder();

        AppendFixedUnit(builder, ref ticks, TimeSpan.TicksPerDay * 7L, "w");
        AppendFixedUnit(builder, ref ticks, TimeSpan.TicksPerDay, "d");
        AppendFixedUnit(builder, ref ticks, TimeSpan.TicksPerHour, "h");
        AppendFixedUnit(builder, ref ticks, TimeSpan.TicksPerMinute, "m");
        AppendFixedUnit(builder, ref ticks, TimeSpan.TicksPerSecond, "s");
        AppendFixedUnit(builder, ref ticks, TimeSpan.TicksPerMillisecond, "ms");
        AppendFixedUnit(builder, ref ticks, 10L, "us");
        AppendFixedUnit(builder, ref ticks, 1L, "ns", scale: 100L);

        if (builder.Length == 0)
        {
            builder.Append("0s");
        }

        if (duration < TimeSpan.Zero)
        {
            builder.Insert(0, '-');
        }

        return builder.ToString();
    }

    private static void AppendWholeUnit(StringBuilder builder, ref long remaining, long unitSize, string suffix)
    {
        if (remaining < unitSize)
        {
            return;
        }

        var amount = remaining / unitSize;
        remaining %= unitSize;
        builder.Append(amount.ToString(CultureInfo.InvariantCulture));
        builder.Append(suffix);
    }

    private static void AppendFixedUnit(StringBuilder builder, ref long remainingTicks, long ticksPerUnit, string suffix, long scale = 1L)
    {
        if (remainingTicks < ticksPerUnit)
        {
            return;
        }

        var amount = remainingTicks / ticksPerUnit;
        remainingTicks %= ticksPerUnit;
        builder.Append((amount * scale).ToString(CultureInfo.InvariantCulture));
        builder.Append(suffix);
    }
}
