using System.Globalization;
using System.Text.RegularExpressions;

namespace Tosh.Core;

public static partial class TemporalParser
{
    private static readonly string[] DateTimeOffsetFormats =
    [
        "O",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mmK",
        "yyyy-MM-dd HH:mm:ss.FFFFFFFK",
        "yyyy-MM-dd HH:mm:ssK",
        "yyyy-MM-dd HH:mmK",
    ];

    private static readonly string[] DateTimeFormats =
    [
        "O",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm",
        "yyyy-MM-dd HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-dd",
    ];

    public static bool TryParseDateTimeOffset(string? text, out DateTimeOffset value)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            value = default;
            return false;
        }

        var trimmed = text.Trim();

        if (DateTimeOffset.TryParseExact(
                trimmed,
                DateTimeOffsetFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                out value))
        {
            return true;
        }

        if (DateTime.TryParseExact(
                trimmed,
                DateTimeFormats,
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

        var trimmed = text.Trim();

        if (TryParseUnitDuration(trimmed, out value))
        {
            return true;
        }

        return TimeSpan.TryParse(trimmed, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseUnitDuration(string text, out TimeSpan value)
    {
        var normalized = text.Replace(" ", string.Empty, StringComparison.Ordinal);
        var matches = DurationPartRegex().Matches(normalized);

        if (matches.Count == 0)
        {
            value = default;
            return false;
        }

        var consumed = string.Concat(matches.Select(match => match.Value));

        if (!string.Equals(consumed, normalized, StringComparison.Ordinal))
        {
            value = default;
            return false;
        }

        decimal totalSeconds = 0m;

        foreach (Match match in matches)
        {
            if (!decimal.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
            {
                value = default;
                return false;
            }

            var unit = match.Groups["unit"].Value.ToLowerInvariant();
            totalSeconds += unit switch
            {
                "w" => numeric * 7m * 24m * 60m * 60m,
                "d" => numeric * 24m * 60m * 60m,
                "h" => numeric * 60m * 60m,
                "m" => numeric * 60m,
                "s" => numeric,
                "ms" => numeric / 1000m,
                _ => throw new InvalidOperationException($"Unsupported duration unit '{unit}'."),
            };
        }

        try
        {
            value = TimeSpan.FromSeconds((double)totalSeconds);
            return true;
        }
        catch
        {
            value = default;
            return false;
        }
    }

    [GeneratedRegex(@"(?<value>[+-]?(?:\d+(?:\.\d+)?|\.\d+))(?<unit>ms|w|d|h|m|s)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DurationPartRegex();
}
