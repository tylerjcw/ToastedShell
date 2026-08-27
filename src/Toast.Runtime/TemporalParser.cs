using System.Globalization;

namespace Tosh.Runtime;

public static class TemporalParser
{
    private static readonly string[] DateTimeOffsetLiteralFormats =
    [
        "O",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mmK",
    ];

    private static readonly string[] DateTimeLiteralFormats =
    [
        "O",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm",
        "yyyy-MM-dd",
    ];

    public static bool TryParseDateTimeOffset(string? text, out DateTimeOffset value)
    {
        if (TryParseDateTimeOffsetLiteral(text, out value))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            value = default;
            return false;
        }

        var trimmed = text.Trim();

        if (DateTimeOffset.TryParse(
                trimmed,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                out value))
        {
            return true;
        }

        if (DateTime.TryParse(
                trimmed,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                out var dateTime))
        {
            value = dateTime.Kind == DateTimeKind.Unspecified
                ? new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Local))
                : new DateTimeOffset(dateTime);
            return true;
        }

        if (TryParseSystemdLocalTimestamp(trimmed, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Parses only the ISO-style forms that ToastScript recognizes as intrinsic
    /// temporal literals. Unlike <see cref="TryParseDateTimeOffset"/>, this
    /// method deliberately has no culture-based fallback: dotted numbers and
    /// other command-friendly date spellings must remain ordinary barewords in
    /// expression position.
    /// </summary>
    public static bool TryParseDateTimeOffsetLiteral(string? text, out DateTimeOffset value)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            value = default;
            return false;
        }

        var trimmed = text.Trim();

        if (DateTimeOffset.TryParseExact(
                trimmed,
                DateTimeOffsetLiteralFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                out value))
        {
            return true;
        }

        if (DateTime.TryParseExact(
                trimmed,
                DateTimeLiteralFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                out var dateTime))
        {
            value = dateTime.Kind == DateTimeKind.Unspecified
                ? new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Local))
                : new DateTimeOffset(dateTime);
            return true;
        }

        value = default;
        return false;
    }

    public static bool TryParseDateTime(string? text, out DateTime value)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            value = default;
            return false;
        }

        if (TryParseDateTimeOffset(text, out var dateTimeOffset))
        {
            value = dateTimeOffset.LocalDateTime;
            return true;
        }

        value = default;
        return false;
    }

    public static bool TryParseDuration(string? text, out TimeSpan value)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            value = default;
            return false;
        }

        if (TryParseTemporalAmount(text, out var amount) &&
            amount.TryAsTimeSpan(out value))
        {
            return true;
        }

        return TimeSpan.TryParse(text.Trim(), CultureInfo.InvariantCulture, out value);
    }

    public static bool TryParseTemporalAmount(string? text, out TemporalAmount value)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            value = default;
            return false;
        }

        var normalized = text.Replace(" ", string.Empty, StringComparison.Ordinal);

        if (string.IsNullOrWhiteSpace(normalized))
        {
            value = default;
            return false;
        }

        long totalMonths = 0;
        decimal totalTicks = 0m;
        var index = 0;
        var inheritedSign = 1;
        var sawAny = false;

        while (index < normalized.Length)
        {
            var sign = inheritedSign;

            if (normalized[index] is '+' or '-')
            {
                sign = normalized[index] == '-' ? -1 : 1;
                inheritedSign = sign;
                index++;
            }

            var numberStart = index;
            var seenDigit = false;
            var seenDecimalPoint = false;

            while (index < normalized.Length)
            {
                var current = normalized[index];

                if (char.IsDigit(current))
                {
                    seenDigit = true;
                    index++;
                    continue;
                }

                if (current == '.' && !seenDecimalPoint)
                {
                    seenDecimalPoint = true;
                    index++;
                    continue;
                }

                break;
            }

            if (!seenDigit)
            {
                value = default;
                return false;
            }

            var numberText = normalized[numberStart..index];

            if (!decimal.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
            {
                value = default;
                return false;
            }

            if (!TryReadUnit(normalized, ref index, out var unit))
            {
                value = default;
                return false;
            }

            switch (unit)
            {
                case "Ta":
                    if (!TryAddCalendarMonths(ref totalMonths, numeric, 12_000_000_000_000L, sign))
                    {
                        value = default;
                        return false;
                    }
                    break;

                case "Ga":
                    if (!TryAddCalendarMonths(ref totalMonths, numeric, 12_000_000_000L, sign))
                    {
                        value = default;
                        return false;
                    }
                    break;

                case "Ma":
                    if (!TryAddCalendarMonths(ref totalMonths, numeric, 12_000_000L, sign))
                    {
                        value = default;
                        return false;
                    }
                    break;

                case "ka":
                    if (!TryAddCalendarMonths(ref totalMonths, numeric, 12_000L, sign))
                    {
                        value = default;
                        return false;
                    }
                    break;

                case "c":
                    if (!TryAddCalendarMonths(ref totalMonths, numeric, 1_200L, sign))
                    {
                        value = default;
                        return false;
                    }
                    break;

                case "da":
                    if (!TryAddCalendarMonths(ref totalMonths, numeric, 120L, sign))
                    {
                        value = default;
                        return false;
                    }
                    break;

                case "y":
                    if (!TryAddCalendarMonths(ref totalMonths, numeric, 12L, sign))
                    {
                        value = default;
                        return false;
                    }
                    break;

                case "mo":
                    if (!TryAddCalendarMonths(ref totalMonths, numeric, 1L, sign))
                    {
                        value = default;
                        return false;
                    }
                    break;

                case "w":
                    totalTicks += sign * numeric * (TimeSpan.TicksPerDay * 7m);
                    break;

                case "d":
                    totalTicks += sign * numeric * TimeSpan.TicksPerDay;
                    break;

                case "h":
                    totalTicks += sign * numeric * TimeSpan.TicksPerHour;
                    break;

                case "m":
                    totalTicks += sign * numeric * TimeSpan.TicksPerMinute;
                    break;

                case "s":
                    totalTicks += sign * numeric * TimeSpan.TicksPerSecond;
                    break;

                case "ms":
                    totalTicks += sign * numeric * TimeSpan.TicksPerMillisecond;
                    break;

                case "us":
                    totalTicks += sign * numeric * 10m;
                    break;

                case "ns":
                    totalTicks += sign * (numeric / 100m);
                    break;

                default:
                    value = default;
                    return false;
            }

            sawAny = true;
        }

        try
        {
            var roundedTicks = decimal.ToInt64(decimal.Round(totalTicks, MidpointRounding.AwayFromZero));
            value = new TemporalAmount(totalMonths, TimeSpan.FromTicks(roundedTicks));
            return sawAny;
        }
        catch
        {
            value = default;
            return false;
        }
    }

    public static bool TryParseExpressionLiteral(string? text, out object? value)
    {
        return IntrinsicLiteralParser.TryParseExpressionLiteral(text, out value);
    }

    private static bool TryReadUnit(string text, ref int index, out string unit)
    {
        foreach (var candidate in OrderedUnits)
        {
            if (!text.AsSpan(index).StartsWith(candidate, StringComparison.Ordinal))
            {
                continue;
            }

            index += candidate.Length;
            unit = candidate;
            return true;
        }

        unit = string.Empty;
        return false;
    }

    private static bool TryParseSystemdLocalTimestamp(string text, out DateTimeOffset value)
    {
        value = default;

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length < 4 ||
            parts[0].Length != 3 ||
            !char.IsLetter(parts[0][0]) ||
            !char.IsLetter(parts[^1][0]))
        {
            return false;
        }

        var candidate = string.Join(" ", parts.Skip(1).Take(parts.Length - 2));

        if (!DateTime.TryParseExact(
                candidate,
                ["yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd HH:mm:ss"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                out var dateTime))
        {
            return false;
        }

        value = dateTime.Kind == DateTimeKind.Unspecified
            ? new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Local))
            : new DateTimeOffset(dateTime);
        return true;
    }

    private static bool TryAddCalendarMonths(ref long totalMonths, decimal numeric, long multiplier, int sign)
    {
        if (numeric != decimal.Truncate(numeric))
        {
            return false;
        }

        try
        {
            var scaled = decimal.ToInt64(numeric);
            totalMonths = checked(totalMonths + (scaled * multiplier * sign));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static readonly string[] OrderedUnits =
    [
        "Ta",
        "Ga",
        "Ma",
        "ka",
        "mo",
        "da",
        "ns",
        "us",
        "ms",
        "y",
        "c",
        "w",
        "d",
        "h",
        "m",
        "s",
    ];
}
