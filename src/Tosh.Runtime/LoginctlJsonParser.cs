using System.Globalization;
using System.Text.Json;

namespace Tosh.Runtime;

public static class LoginctlJsonParser
{
    public static IReadOnlyList<SystemdLoginSessionInfo> ParseSessionList(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<SystemdLoginSessionInfo>();
        }

        using var document = JsonDocument.Parse(json);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Expected a JSON array of loginctl session rows.");
        }

        return document.RootElement
            .EnumerateArray()
            .Select(ParseSession)
            .ToArray();
    }

    public static IReadOnlyList<SystemdLoginUserInfo> ParseUserList(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<SystemdLoginUserInfo>();
        }

        using var document = JsonDocument.Parse(json);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Expected a JSON array of loginctl user rows.");
        }

        return document.RootElement
            .EnumerateArray()
            .Select(ParseUser)
            .ToArray();
    }

    public static IReadOnlyList<SystemdLoginSeatInfo> ParseSeatList(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<SystemdLoginSeatInfo>();
        }

        using var document = JsonDocument.Parse(json);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Expected a JSON array of loginctl seat rows.");
        }

        return document.RootElement
            .EnumerateArray()
            .Select(ParseSeat)
            .ToArray();
    }

    public static IReadOnlyList<SystemdPropertySet> ParseShowOutput(string text, string identityPropertyName)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<SystemdPropertySet>();
        }

        var results = new List<SystemdPropertySet>();
        var current = new List<KeyValuePair<string, object?>>();
        var sawIdentity = false;

        foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.TrimEnd();

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');

            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex];
            var value = line[(separatorIndex + 1)..];

            if (sawIdentity &&
                string.Equals(key, identityPropertyName, StringComparison.OrdinalIgnoreCase) &&
                current.Count > 0)
            {
                results.Add(new SystemdPropertySet(current));
                current = [];
            }

            sawIdentity |= string.Equals(key, identityPropertyName, StringComparison.OrdinalIgnoreCase);
            current.Add(new KeyValuePair<string, object?>(key, SystemdParsingUtilities.ParsePropertyValue(key, value)));
        }

        if (current.Count > 0)
        {
            results.Add(new SystemdPropertySet(current));
        }

        return results;
    }

    private static SystemdLoginSessionInfo ParseSession(JsonElement element)
    {
        return new SystemdLoginSessionInfo(
            GetRequiredString(element, "session"),
            GetRequiredInt32(element, "uid"),
            GetRequiredString(element, "user"),
            GetOptionalString(element, "seat"),
            GetOptionalInt32(element, "leader"),
            GetOptionalString(element, "class"),
            GetOptionalString(element, "tty"),
            GetOptionalBoolean(element, "idle") ?? false,
            GetOptionalDateTimeOffset(element, "since"));
    }

    private static SystemdLoginUserInfo ParseUser(JsonElement element)
    {
        return new SystemdLoginUserInfo(
            GetRequiredInt32(element, "uid"),
            GetRequiredString(element, "user"),
            GetOptionalBoolean(element, "linger") ?? false,
            GetOptionalString(element, "state"));
    }

    private static SystemdLoginSeatInfo ParseSeat(JsonElement element)
    {
        return new SystemdLoginSeatInfo(GetRequiredString(element, "seat"));
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new InvalidOperationException($"Expected property '{propertyName}' in a loginctl JSON row.");
        }

        return property.GetString() ?? string.Empty;
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.GetString();
    }

    private static int GetRequiredInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new InvalidOperationException($"Expected property '{propertyName}' in a loginctl JSON row.");
        }

        return property.GetInt32();
    }

    private static int? GetOptionalInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.GetInt32(),
            JsonValueKind.String when int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    private static bool? GetOptionalBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(property.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static DateTimeOffset? GetOptionalDateTimeOffset(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String when TemporalParser.TryParseDateTimeOffset(property.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }
}
