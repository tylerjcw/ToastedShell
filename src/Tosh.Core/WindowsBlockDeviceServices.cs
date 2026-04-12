using System.Runtime.Versioning;

namespace Tosh.Core;

/// <summary>
/// Windows implementation of block device enumeration.
/// Maps DriveInfo (logical volumes) to BlockDeviceInfo objects.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsBlockDeviceServices
{
    public static IReadOnlyList<BlockDeviceInfo> GetBlockDevices()
    {
        var result = new List<BlockDeviceInfo>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            var device = BuildBlockDeviceInfo(drive);

            if (device is not null)
            {
                result.Add(device);
            }
        }

        return result;
    }

    private static BlockDeviceInfo? BuildBlockDeviceInfo(DriveInfo drive)
    {
        try
        {
            var name = drive.Name.TrimEnd('\\', '/');
            var path = drive.Name.TrimEnd('\\', '/');
            var mountPoint = drive.RootDirectory.FullName;

            StorageSize? size = TryGetSize(() => drive.TotalSize);
            StorageSize? available = TryGetSize(() => drive.AvailableFreeSpace);
            StorageSize? used = TryGetUsed(drive);
            int? usePercent = ComputeUsePercent(used, available);

            string? fsType = null;
            string? label = null;

            try { fsType = drive.DriveFormat; } catch { }
            try { label = drive.VolumeLabel; } catch { }

            var type = MapDriveType(drive.DriveType);

            return new BlockDeviceInfo
            {
                Name = name,
                Path = path,
                Type = type,
                MountPoint = mountPoint,
                MountPoints = [mountPoint],
                Size = size ?? StorageSize.FromBytes(0),
                FileSystemType = fsType,
                Label = label,
                FileSystemSize = size,
                FileSystemUsed = used,
                FileSystemAvailable = available,
                FileSystemUsePercent = usePercent,
                Removable = drive.DriveType == DriveType.Removable,
                ReadOnly = !drive.IsReady,
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? MapDriveType(DriveType type)
    {
        return type switch
        {
            DriveType.Fixed => "disk",
            DriveType.Removable => "disk",
            DriveType.CDRom => "rom",
            DriveType.Network => "disk",
            DriveType.Ram => "disk",
            _ => null,
        };
    }

    private static StorageSize? TryGetSize(Func<long> getBytes)
    {
        try { return StorageSize.FromBytes(getBytes()); }
        catch { return null; }
    }

    private static StorageSize? TryGetUsed(DriveInfo drive)
    {
        try
        {
            var total = drive.TotalSize;
            var free = drive.TotalFreeSpace;
            return StorageSize.FromBytes(Math.Max(0, total - free));
        }
        catch { return null; }
    }

    private static int? ComputeUsePercent(StorageSize? used, StorageSize? available)
    {
        if (used is not StorageSize usedVal || available is not StorageSize availVal) return null;

        var denominator = usedVal.Bytes + availVal.Bytes;

        if (denominator <= 0) return null;

        return (int)Math.Round(100d * usedVal.Bytes / denominator, MidpointRounding.AwayFromZero);
    }
}
