using System.Text.Json;

namespace Tosh.Core;

public static class SystemctlJsonParser
{
    public static IReadOnlyList<SystemdUnitInfo> ParseUnitList(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<SystemdUnitInfo>();
        }

        using var document = JsonDocument.Parse(json);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Expected a JSON array of systemd unit rows.");
        }

        return document.RootElement
            .EnumerateArray()
            .Select(ParseUnit)
            .ToArray();
    }

    public static IReadOnlyList<SystemdUnitFileInfo> ParseUnitFileList(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<SystemdUnitFileInfo>();
        }

        using var document = JsonDocument.Parse(json);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Expected a JSON array of systemd unit-file rows.");
        }

        return document.RootElement
            .EnumerateArray()
            .Select(ParseUnitFile)
            .ToArray();
    }

    public static IReadOnlyList<SystemdUnitPropertySet> ParseShowOutput(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<SystemdUnitPropertySet>();
        }

        var results = new List<SystemdUnitPropertySet>();
        var current = new List<KeyValuePair<string, object?>>();
        var sawId = false;

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

            if (sawId &&
                string.Equals(key, "Id", StringComparison.OrdinalIgnoreCase) &&
                current.Count > 0)
            {
                results.Add(new SystemdUnitPropertySet(current));
                current = [];
            }

            sawId |= string.Equals(key, "Id", StringComparison.OrdinalIgnoreCase);
            current.Add(new KeyValuePair<string, object?>(key, SystemdParsingUtilities.ParseSystemctlValue(key, value)));
        }

        if (current.Count > 0)
        {
            results.Add(new SystemdUnitPropertySet(current));
        }

        return results;
    }

    private static SystemdUnitInfo ParseUnit(JsonElement element)
    {
        var unit = GetRequiredString(element, "unit");
        var load = GetRequiredString(element, "load");
        var active = GetRequiredString(element, "active");
        var sub = GetRequiredString(element, "sub");
        var description = GetOptionalString(element, "description");

        return new SystemdUnitInfo(
            SystemdParsingUtilities.DecodeEscapedText(unit),
            SystemdParsingUtilities.DecodeEscapedText(load),
            SystemdParsingUtilities.DecodeEscapedText(active),
            SystemdParsingUtilities.DecodeEscapedText(sub),
            description is null ? null : SystemdParsingUtilities.DecodeEscapedText(description));
    }

    private static SystemdUnitFileInfo ParseUnitFile(JsonElement element)
    {
        var unitFile = GetRequiredString(element, "unit_file");
        var state = GetRequiredString(element, "state");
        var preset = GetOptionalString(element, "preset");

        return new SystemdUnitFileInfo(
            SystemdParsingUtilities.DecodeEscapedText(unitFile),
            SystemdParsingUtilities.DecodeEscapedText(state),
            preset is null ? null : SystemdParsingUtilities.DecodeEscapedText(preset));
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new InvalidOperationException($"Expected property '{propertyName}' in a systemctl JSON row.");
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
}
