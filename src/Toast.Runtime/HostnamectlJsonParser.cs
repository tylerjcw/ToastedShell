using System.Text.Json;

namespace Tosh.Runtime;

public static class HostnamectlJsonParser
{
    public static SystemdHostInfo ParseStatus(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("The hostnamectl JSON output was empty.");
        }

        using var document = JsonDocument.Parse(json);

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Expected a hostnamectl JSON object.");
        }

        var properties = document.RootElement
            .EnumerateObject()
            .Select(property => new KeyValuePair<string, object?>(
                property.Name,
                SystemdParsingUtilities.ConvertSystemdJsonValue(property.Name, property.Value)))
            .ToArray();

        return new SystemdHostInfo(properties);
    }
}
