using System.Text.Json;

namespace Tosh.Core;

public static class FindmntJsonParser
{
    public static IReadOnlyList<MountInfo> ParseMounts(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<MountInfo>();
        }

        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("filesystems", out var filesystemsElement) ||
            filesystemsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("The `findmnt --json` output did not contain a 'filesystems' array.");
        }

        return filesystemsElement
            .EnumerateArray()
            .Select(ParseMount)
            .ToArray();
    }

    private static MountInfo ParseMount(JsonElement element)
    {
        var children = Array.Empty<MountInfo>();

        if (element.TryGetProperty("children", out var childrenElement) &&
            childrenElement.ValueKind == JsonValueKind.Array)
        {
            children = childrenElement
                .EnumerateArray()
                .Select(ParseMount)
                .ToArray();
        }

        return new MountInfo
        {
            Target = GetString(element, "target") ?? string.Empty,
            Source = GetString(element, "source"),
            Sources = GetStringArray(element, "sources"),
            FileSystemType = GetString(element, "fstype"),
            FileSystemRoot = GetString(element, "fsroot"),
            Options = GetString(element, "options"),
            FileSystemOptions = GetString(element, "fs-options"),
            VfsOptions = GetString(element, "vfs-options"),
            OptionalFields = GetString(element, "opt-fields"),
            Propagation = GetString(element, "propagation"),
            Label = GetString(element, "label"),
            Uuid = GetString(element, "uuid"),
            PartitionLabel = GetString(element, "partlabel"),
            PartitionUuid = GetString(element, "partuuid"),
            MajorMinor = GetString(element, "maj:min"),
            Size = GetNullableSize(element, "size"),
            Used = GetNullableSize(element, "used"),
            Available = GetNullableSize(element, "avail"),
            UsePercent = GetPercent(element, "use%"),
            InodesAvailable = GetInt64(element, "ino.avail"),
            InodesTotal = GetInt64(element, "ino.total"),
            InodesUsed = GetInt64(element, "ino.used"),
            InodeUsePercent = GetPercent(element, "ino.use%"),
            Id = GetInt32(element, "id"),
            ParentId = GetInt32(element, "parent"),
            TaskId = GetInt32(element, "tid"),
            UniqueId = GetInt64(element, "uniq-id"),
            FrequencyDays = GetInt32(element, "freq"),
            PassNumber = GetInt32(element, "passno"),
            Children = children,
        };
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => bool.TrueString.ToLowerInvariant(),
            JsonValueKind.False => bool.FalseString.ToLowerInvariant(),
            _ => property.GetRawText(),
        };
    }

    private static string[] GetStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return property
            .EnumerateArray()
            .Select(GetStringValue)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray()!;
    }

    private static string? GetStringValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => bool.TrueString.ToLowerInvariant(),
            JsonValueKind.False => bool.FalseString.ToLowerInvariant(),
            _ => element.GetRawText(),
        };
    }

    private static StorageSize? GetNullableSize(JsonElement element, string propertyName)
    {
        var bytes = GetInt64(element, propertyName);
        return bytes is long value ? StorageSize.FromBytes(value) : null;
    }

    private static int? GetInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var direct))
        {
            return direct;
        }

        return int.TryParse(GetStringValue(property), out var parsed) ? parsed : null;
    }

    private static long? GetInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var direct))
        {
            return direct;
        }

        return long.TryParse(GetStringValue(property), out var parsed) ? parsed : null;
    }

    private static int? GetPercent(JsonElement element, string propertyName)
    {
        var text = GetString(element, propertyName);

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();

        if (text.EndsWith('%'))
        {
            text = text[..^1];
        }

        return int.TryParse(text, out var parsed) ? parsed : null;
    }
}
