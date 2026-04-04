namespace Tosh.Core;

public static class FileSystemUsageUtilities
{
    public static FileSystemUsageInfo? FindContainingMount(IReadOnlyList<FileSystemUsageInfo> entries, string path)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return entries
            .Where(entry => PathIsWithinMount(path, entry.MountedOn))
            .OrderByDescending(entry => entry.MountedOn.Length)
            .FirstOrDefault();
    }

    public static bool PathIsWithinMount(string path, string mountPoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(mountPoint);

        if (string.Equals(path, mountPoint, StringComparison.Ordinal))
        {
            return true;
        }

        if (mountPoint == Path.DirectorySeparatorChar.ToString())
        {
            return path.StartsWith(mountPoint, StringComparison.Ordinal);
        }

        return path.StartsWith(mountPoint + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    public static FileSystemUsageInfo CreateTotalRow(IEnumerable<FileSystemUsageInfo> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var items = entries.ToArray();

        if (items.Length == 0)
        {
            throw new InvalidOperationException("Cannot build a total row for an empty filesystem usage set.");
        }

        var size = SumStorageSize(items.Select(item => item.Size));
        var used = SumStorageSize(items.Select(item => item.Used));
        var available = SumStorageSize(items.Select(item => item.Available));
        var usePercent = CalculateUsePercent(used, available);
        var driveType = items.Select(item => item.DriveType).Distinct().Count() == 1 ? items[0].DriveType : null;
        var type = items.Select(item => item.Type).Where(static value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
            ? items[0].Type
            : null;

        return new FileSystemUsageInfo(
            "total",
            "-",
            type,
            size,
            used,
            available,
            usePercent,
            driveType,
            items.All(item => item.IsLocal),
            MountRoot: null,
            RequestedPath: null,
            IsTotal: true);
    }

    public static IReadOnlyList<FileSystemUsageInfo> GetDefaultVisibleEntries(IEnumerable<FileSystemUsageInfo> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var items = entries
            .Where(entry => entry.Size is { Bytes: > 0 })
            .OrderBy(entry => entry.MountedOn, StringComparer.Ordinal)
            .ToList();

        var rootMountedSources = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in items)
        {
            if (string.Equals(entry.MountRoot, "/", StringComparison.Ordinal))
            {
                rootMountedSources.Add(entry.FileSystem);
            }
        }

        return items
            .Where(entry => !IsDuplicateSubrootMount(entry, rootMountedSources))
            .ToArray();
    }

    private static bool IsDuplicateSubrootMount(FileSystemUsageInfo entry, IReadOnlySet<string> rootMountedSources)
    {
        if (string.IsNullOrWhiteSpace(entry.MountRoot) ||
            string.Equals(entry.MountRoot, "/", StringComparison.Ordinal))
        {
            return false;
        }

        return rootMountedSources.Contains(entry.FileSystem);
    }

    private static StorageSize? SumStorageSize(IEnumerable<StorageSize?> values)
    {
        var total = 0L;
        var any = false;

        foreach (var value in values)
        {
            if (value is null)
            {
                continue;
            }

            any = true;
            total += value.Value.Bytes;
        }

        return any ? StorageSize.FromBytes(total) : null;
    }

    private static int? CalculateUsePercent(StorageSize? used, StorageSize? available)
    {
        if (used is null || available is null)
        {
            return null;
        }

        var denominator = used.Value.Bytes + available.Value.Bytes;

        if (denominator <= 0)
        {
            return null;
        }

        return (int)Math.Round(100d * used.Value.Bytes / denominator, MidpointRounding.AwayFromZero);
    }
}
