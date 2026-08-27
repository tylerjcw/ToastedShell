using System.Runtime.Versioning;

namespace Tosh.Runtime;

/// <summary>
/// Windows implementation of mount point enumeration.
/// Maps DriveInfo (logical volumes) to MountInfo objects.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsMountServices
{
    public static IReadOnlyList<MountInfo> GetMounts()
    {
        var result = new List<MountInfo>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            var mount = BuildMountInfo(drive);

            if (mount is not null)
            {
                result.Add(mount);
            }
        }

        return result;
    }

    private static MountInfo? BuildMountInfo(DriveInfo drive)
    {
        try
        {
            var target = drive.RootDirectory.FullName;
            var source = drive.Name.TrimEnd('\\', '/');

            StorageSize? size = TryGetSize(() => drive.TotalSize);
            StorageSize? available = TryGetSize(() => drive.AvailableFreeSpace);
            StorageSize? used = TryGetUsed(drive);
            int? usePercent = ComputeUsePercent(used, available);

            string? fsType = null;
            string? label = null;

            try { fsType = drive.DriveFormat; } catch { }
            try { label = drive.VolumeLabel; } catch { }

            return new MountInfo
            {
                Target = target,
                Source = source,
                Sources = [source],
                FileSystemType = fsType,
                Label = label,
                Size = size,
                Used = used,
                Available = available,
                UsePercent = usePercent,
            };
        }
        catch
        {
            return null;
        }
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
