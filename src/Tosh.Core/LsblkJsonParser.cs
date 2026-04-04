using System.Text.Json;

namespace Tosh.Core;

public static class LsblkJsonParser
{
    public static IReadOnlyList<BlockDeviceInfo> ParseDevices(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<BlockDeviceInfo>();
        }

        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("blockdevices", out var devicesElement) ||
            devicesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("The `lsblk --json` output did not contain a 'blockdevices' array.");
        }

        var devices = new List<BlockDeviceInfo>();

        foreach (var element in devicesElement.EnumerateArray())
        {
            devices.Add(ParseDevice(element));
        }

        return devices;
    }

    private static BlockDeviceInfo ParseDevice(JsonElement element)
    {
        var children = Array.Empty<BlockDeviceInfo>();

        if (element.TryGetProperty("children", out var childrenElement) &&
            childrenElement.ValueKind == JsonValueKind.Array)
        {
            children = childrenElement
                .EnumerateArray()
                .Select(ParseDevice)
                .ToArray();
        }

        return new BlockDeviceInfo
        {
            Name = GetString(element, "name") ?? string.Empty,
            Path = GetString(element, "path"),
            KernelName = GetString(element, "kname"),
            ParentKernelName = GetString(element, "pkname"),
            Type = GetString(element, "type"),
            MajorMinor = GetString(element, "maj:min"),
            Major = GetInt32(element, "maj"),
            Minor = GetInt32(element, "min"),
            Size = GetSize(element, "size"),
            FileSystemSize = GetNullableSize(element, "fssize"),
            FileSystemUsed = GetNullableSize(element, "fsused"),
            FileSystemAvailable = GetNullableSize(element, "fsavail"),
            FileSystemUsePercent = GetPercent(element, "fsuse%"),
            FileSystemType = GetString(element, "fstype"),
            FileSystemVersion = GetString(element, "fsver"),
            Label = GetString(element, "label"),
            Uuid = GetString(element, "uuid"),
            PartitionLabel = GetString(element, "partlabel"),
            PartitionUuid = GetString(element, "partuuid"),
            PartitionType = GetString(element, "parttype"),
            PartitionTypeName = GetString(element, "parttypename"),
            PartitionNumber = GetInt32(element, "partn"),
            PartitionTableType = GetString(element, "pttype"),
            PartitionTableUuid = GetString(element, "ptuuid"),
            Model = Normalize(GetString(element, "model")),
            Serial = Normalize(GetString(element, "serial")),
            Vendor = Normalize(GetString(element, "vendor")),
            Transport = GetString(element, "tran"),
            State = GetString(element, "state"),
            Owner = GetString(element, "owner"),
            Group = GetString(element, "group"),
            Mode = GetString(element, "mode"),
            Hctl = GetString(element, "hctl"),
            Scheduler = Normalize(GetString(element, "sched")),
            Subsystems = GetString(element, "subsystems"),
            ReadOnly = GetBool(element, "ro"),
            Removable = GetBool(element, "rm"),
            HotPlug = GetBool(element, "hotplug"),
            Rotational = GetBool(element, "rota"),
            Random = GetBool(element, "rand"),
            Dax = GetBool(element, "dax"),
            DiscardZero = GetBool(element, "disc-zero"),
            Alignment = GetInt32(element, "alignment"),
            DiscardAlignment = GetNullableSize(element, "disc-aln"),
            DiscardGranularity = GetNullableSize(element, "disc-gran"),
            DiscardMax = GetNullableSize(element, "disc-max"),
            DiskSequence = GetInt32(element, "disk-seq"),
            LogicalSectorSize = GetInt32(element, "log-sec"),
            PhysicalSectorSize = GetInt32(element, "phy-sec"),
            MinimumIoSize = GetInt32(element, "min-io"),
            OptimalIoSize = GetInt32(element, "opt-io"),
            RequestQueueSize = GetInt32(element, "rq-size"),
            ReadAhead = GetInt32(element, "ra"),
            Start = GetInt64(element, "start"),
            WSame = GetNullableSize(element, "wsame"),
            Zoned = GetString(element, "zoned"),
            ZoneSize = GetNullableSize(element, "zone-sz"),
            ZoneWriteGranularity = GetNullableSize(element, "zone-wgran"),
            ZoneAppendSize = GetNullableSize(element, "zone-app"),
            ZoneCount = GetInt32(element, "zone-nr"),
            ZoneOpenMax = GetInt32(element, "zone-omax"),
            ZoneActiveMax = GetInt32(element, "zone-amax"),
            MountPoint = GetString(element, "mountpoint"),
            MountPoints = GetStringArray(element, "mountpoints"),
            FileSystemRoots = GetStringArray(element, "fsroots"),
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
            .Select(item => GetStringValue(item))
            .Where(item => !string.IsNullOrWhiteSpace(item))
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

    private static bool GetBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => property.TryGetInt64(out var number) && number != 0,
            JsonValueKind.String => bool.TryParse(property.GetString(), out var parsed) && parsed,
            _ => false,
        };
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

    private static StorageSize GetSize(JsonElement element, string propertyName)
    {
        return GetNullableSize(element, propertyName) ?? StorageSize.FromBytes(0);
    }

    private static StorageSize? GetNullableSize(JsonElement element, string propertyName)
    {
        var bytes = GetInt64(element, propertyName);
        return bytes is long value ? StorageSize.FromBytes(value) : null;
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
