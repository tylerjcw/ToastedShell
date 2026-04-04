using System.Globalization;
using System.Text.Json;

namespace Tosh.Core;

public static class TreeJsonParser
{
    public static IReadOnlyList<TreeEntryInfo> Parse(string json, string? basePath = null)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<TreeEntryInfo>();
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("The `tree --json` output is not a JSON array.");
        }

        var entries = new List<TreeEntryInfo>();

        foreach (var element in root.EnumerateArray())
        {
            if (IsReportEntry(element))
            {
                continue;
            }

            entries.Add(ParseEntry(element, basePath, depth: 0));
        }

        return entries;
    }

    private static TreeEntryInfo ParseEntry(JsonElement element, string? parentPath, int depth)
    {
        var rawName = GetString(element, "name") ?? string.Empty;
        var type = NormalizeType(GetString(element, "type"));
        var name = rawName.TrimEnd('/');
        var fullPath = BuildFullPath(parentPath, rawName, type);

        var children = Array.Empty<TreeEntryInfo>();

        if (element.TryGetProperty("contents", out var contentsElement) &&
            contentsElement.ValueKind == JsonValueKind.Array)
        {
            var childList = new List<TreeEntryInfo>();

            foreach (var child in contentsElement.EnumerateArray())
            {
                if (!IsReportEntry(child))
                {
                    childList.Add(ParseEntry(child, fullPath, depth + 1));
                }
            }

            children = childList.ToArray();
        }

        return new TreeEntryInfo
        {
            Name = name,
            Type = type,
            FullPath = fullPath,
            Mode = GetString(element, "mode"),
            Permissions = GetString(element, "prot"),
            User = GetString(element, "user"),
            Group = GetString(element, "group"),
            Size = GetSize(element, "size"),
            Modified = GetTimestamp(element, "time"),
            Inode = GetInt32(element, "inode"),
            DeviceId = GetInt32(element, "dev"),
            NumLinks = GetInt32(element, "nlink"),
            LinkTarget = GetString(element, "target"),
            Depth = depth,
            Children = children,
        };
    }

    private static string? NormalizeType(string? type)
    {
        return type?.ToLowerInvariant() switch
        {
            "directory" => "dir",
            "file" => "file",
            "link" => "link",
            _ => type,
        };
    }

    private static bool IsReportEntry(JsonElement element)
    {
        return element.TryGetProperty("type", out var typeElement) &&
               typeElement.ValueKind == JsonValueKind.String &&
               string.Equals(typeElement.GetString(), "report", StringComparison.OrdinalIgnoreCase);
    }

    private static string? BuildFullPath(string? parentPath, string name, string? type)
    {
        if (parentPath is null)
        {
            return name;
        }

        if (name.StartsWith('/'))
        {
            return name;
        }

        var basePath = parentPath.TrimEnd('/');
        return $"{basePath}/{name}";
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int? GetInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var result) => result,
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    private static StorageSize? GetSize(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var bytes) => StorageSize.FromBytes(bytes),
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => StorageSize.FromBytes(parsed),
            _ => null,
        };
    }

    private static DateTimeOffset? GetTimestamp(JsonElement element, string propertyName)
    {
        var text = GetString(element, propertyName);

        if (text is null)
        {
            return null;
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var result))
        {
            return result;
        }

        return null;
    }
}
