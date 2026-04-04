using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tosh.Core;

internal static partial class SystemdParsingUtilities
{
    private static readonly HashSet<string> ListLikePropertyKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Names",
        "Requires",
        "Wants",
        "WantedBy",
        "Conflicts",
        "Before",
        "After",
        "Documentation",
        "TriggeredBy",
        "Triggers",
        "PropagatesReloadTo",
        "ReloadPropagatedFrom",
        "JoinsNamespaceOf",
        "RequiresMountsFor",
        "OnSuccess",
        "OnFailure",
        "Sessions",
    };

    public static string DecodeEscapedText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        return HexEscapeRegex().Replace(
            value,
            static match => ((char)Convert.ToInt32(match.Groups[1].Value, 16)).ToString(CultureInfo.InvariantCulture));
    }

    public static string GetUnitType(string? unit)
    {
        if (string.IsNullOrWhiteSpace(unit))
        {
            return "unknown";
        }

        var dotIndex = unit.LastIndexOf('.');
        return dotIndex >= 0 && dotIndex < unit.Length - 1
            ? unit[(dotIndex + 1)..]
            : "unknown";
    }

    public static bool TryParseCompactGuid(string? text, out Guid value)
    {
        value = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();

        return Guid.TryParseExact(trimmed, "N", out value) ||
               Guid.TryParse(trimmed, out value);
    }

    public static string GetJournalPriorityName(int? priority)
    {
        return priority switch
        {
            0 => "emerg",
            1 => "alert",
            2 => "crit",
            3 => "err",
            4 => "warning",
            5 => "notice",
            6 => "info",
            7 => "debug",
            _ => priority?.ToString(CultureInfo.InvariantCulture) ?? "<unknown>",
        };
    }

    public static object? ParseSystemctlValue(string key, string? rawValue)
        => ParsePropertyValue(key, rawValue);

    public static object? ParsePropertyValue(string key, string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return string.Empty;
        }

        var trimmed = rawValue.Trim();

        if (trimmed is "[not set]" or "[no data]" or "[n/a]")
        {
            return null;
        }

        if (ListLikePropertyKeys.Contains(key))
        {
            return SplitListValue(trimmed);
        }

        if (string.Equals(trimmed, "yes", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(trimmed, "no", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (key.EndsWith("TimestampMonotonic", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var monotonicMicros))
        {
            return TemporalAmount.FromTimeSpan(TimeSpan.FromTicks(monotonicMicros * 10L));
        }

        if ((key.EndsWith("SinceHintMonotonic", StringComparison.OrdinalIgnoreCase) ||
             key.EndsWith("Monotonic", StringComparison.OrdinalIgnoreCase)) &&
            long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out monotonicMicros))
        {
            return TemporalAmount.FromTimeSpan(TimeSpan.FromTicks(monotonicMicros * 10L));
        }

        if (key.EndsWith("Timestamp", StringComparison.OrdinalIgnoreCase) &&
            TemporalParser.TryParseDateTimeOffset(trimmed, out var timestamp))
        {
            return timestamp;
        }

        if (key.EndsWith("USec", StringComparison.OrdinalIgnoreCase))
        {
            if (TemporalParser.TryParseTemporalAmount(trimmed, out var amount))
            {
                return amount.IsPureTimeSpan ? amount.Duration : amount;
            }

            if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros))
            {
                return TemporalAmount.FromTimeSpan(TimeSpan.FromTicks(micros * 10L));
            }
        }

        if (key.EndsWith("NSec", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nanos))
        {
            return TemporalAmount.FromTimeSpan(TimeSpan.FromTicks(nanos / 100L));
        }

        if (LooksLikeByteField(key) &&
            long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes))
        {
            return StorageSize.FromBytes(bytes);
        }

        if (TryParseCompactGuid(trimmed, out var guid) &&
            key.EndsWith("ID", StringComparison.OrdinalIgnoreCase))
        {
            return guid;
        }

        if (LooksLikeUnixEpochMicrosDateField(key) &&
            long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epochMicros))
        {
            return DateTimeOffset.UnixEpoch.AddTicks(epochMicros * 10L);
        }

        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            return integer is >= int.MinValue and <= int.MaxValue
                ? (int)integer
                : integer;
        }

        return DecodeEscapedText(trimmed);
    }

    public static object? ConvertSystemdJsonValue(string key, JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.Array => ConvertJsonArray(key, value),
            JsonValueKind.Object => value.EnumerateObject()
                .Select(property => new KeyValuePair<string, object?>(
                    property.Name,
                    ConvertSystemdJsonValue(property.Name, property.Value)))
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => ConvertSystemdJsonNumber(key, value),
            JsonValueKind.String => ParseSystemdJsonStringValue(key, value.GetString()),
            _ => value.GetRawText(),
        };
    }

    public static object? ConvertJournalJsonValue(string key, JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.Array => value.EnumerateArray()
                .Select(item => ConvertJournalJsonValue(key, item))
                .ToArray(),
            JsonValueKind.String => ParseJournalStringValue(key, value.GetString()),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt64(out var integer) => integer is >= int.MinValue and <= int.MaxValue ? (int)integer : integer,
            JsonValueKind.Number when value.TryGetDouble(out var floating) => floating,
            _ => value.GetRawText(),
        };
    }

    private static object? ParseJournalStringValue(string key, string? rawValue)
    {
        if (rawValue is null)
        {
            return null;
        }

        if (key is "__REALTIME_TIMESTAMP" or "_SOURCE_REALTIME_TIMESTAMP" &&
            long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epochMicros))
        {
            return DateTimeOffset.UnixEpoch.AddTicks(epochMicros * 10L);
        }

        if (key is "__MONOTONIC_TIMESTAMP" or "_SOURCE_MONOTONIC_TIMESTAMP" or "_SOURCE_BOOTTIME_TIMESTAMP" &&
            long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var monotonicMicros))
        {
            return TemporalAmount.FromTimeSpan(TimeSpan.FromTicks(monotonicMicros * 10L));
        }

        if (key is "PRIORITY" or "_PID" or "_UID" or "_GID" or "SYSLOG_FACILITY" or "CODE_LINE" &&
            int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            return integer;
        }

        if (key is "__SEQNUM" &&
            long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence))
        {
            return sequence;
        }

        if (TryParseCompactGuid(rawValue, out var guid) &&
            key.EndsWith("ID", StringComparison.OrdinalIgnoreCase))
        {
            return guid;
        }

        return rawValue;
    }

    private static object? ParseSystemdJsonStringValue(string key, string? rawValue)
    {
        if (rawValue is null)
        {
            return null;
        }

        var decoded = DecodeEscapedText(rawValue);

        if (TryParseCompactGuid(decoded, out var guid) &&
            key.EndsWith("ID", StringComparison.OrdinalIgnoreCase))
        {
            return guid;
        }

        if (key.EndsWith("URL", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(decoded, UriKind.Absolute, out var uri))
        {
            return uri;
        }

        if (key.Contains("FancyName", StringComparison.OrdinalIgnoreCase))
        {
            return StripAnsiEscapeSequences(decoded);
        }

        return decoded;
    }

    private static object? ConvertJsonArray(string key, JsonElement value)
    {
        var items = value.EnumerateArray()
            .Select(item => ConvertSystemdJsonValue(key, item))
            .ToArray();

        return items.All(item => item is string or null)
            ? items.OfType<string>().ToArray()
            : items;
    }

    private static object? ConvertSystemdJsonNumber(string key, JsonElement value)
    {
        if (value.TryGetInt64(out var integer))
        {
            if (LooksLikeUnixEpochMicrosDateField(key))
            {
                return DateTimeOffset.UnixEpoch.AddTicks(integer * 10L);
            }

            return integer is >= int.MinValue and <= int.MaxValue
                ? (int)integer
                : integer;
        }

        if (value.TryGetDouble(out var floating))
        {
            return floating;
        }

        return value.GetRawText();
    }

    private static bool LooksLikeByteField(string key)
    {
        return key.Contains("Memory", StringComparison.OrdinalIgnoreCase) ||
               key.EndsWith("Bytes", StringComparison.OrdinalIgnoreCase) ||
               key.StartsWith("IPIngress", StringComparison.OrdinalIgnoreCase) ||
               key.StartsWith("IPEgress", StringComparison.OrdinalIgnoreCase) ||
               key.StartsWith("IORead", StringComparison.OrdinalIgnoreCase) ||
               key.StartsWith("IOWrite", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeUnixEpochMicrosDateField(string key)
    {
        return string.Equals(key, "FirmwareDate", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> SplitListValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        return ListTokenRegex()
            .Matches(value)
            .Select(match => match.Value.Trim())
            .Where(token => token.Length > 0)
            .Select(Unquote)
            .ToArray();
    }

    private static string Unquote(string value)
    {
        return value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value[1..^1]
            : value;
    }

    [GeneratedRegex("\\\\x([0-9A-Fa-f]{2})", RegexOptions.Compiled)]
    private static partial Regex HexEscapeRegex();

    [GeneratedRegex("\"(?:\\\\.|[^\"])*\"|\\S+", RegexOptions.Compiled)]
    private static partial Regex ListTokenRegex();

    [GeneratedRegex("\u001B\\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled)]
    private static partial Regex AnsiEscapeRegex();

    private static string StripAnsiEscapeSequences(string value)
        => AnsiEscapeRegex().Replace(value, string.Empty);
}
