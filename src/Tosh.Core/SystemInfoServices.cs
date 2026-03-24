using System.Globalization;

namespace Tosh.Core;

internal static class SystemInfoServices
{
    public static IReadOnlyList<MemoryUsageInfo> GetMemoryUsage()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/proc/meminfo"))
        {
            return Array.Empty<MemoryUsageInfo>();
        }

        var values = File.ReadLines("/proc/meminfo")
            .Select(line => line.Split(':', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => ParseMemInfoBytes(parts[1]), StringComparer.Ordinal);

        var buffers = GetStorage(values, "Buffers");
        var cached = GetStorage(values, "Cached");
        var reclaimable = GetStorage(values, "SReclaimable");
        var buffCache = Sum(buffers, cached, reclaimable);

        var memTotal = GetStorage(values, "MemTotal");
        var memFree = GetStorage(values, "MemFree");
        var memAvailable = GetStorage(values, "MemAvailable");
        var memUsed = Subtract(memTotal, memFree, buffCache);

        var swapTotal = GetStorage(values, "SwapTotal");
        var swapFree = GetStorage(values, "SwapFree");
        var swapUsed = Subtract(swapTotal, swapFree);

        return
        [
            new MemoryUsageInfo("Mem", memTotal, memUsed, memFree, GetStorage(values, "Shmem"), buffCache, memAvailable),
            new MemoryUsageInfo("Swap", swapTotal, swapUsed, swapFree, null, null, null),
        ];
    }

    public static SystemUptimeInfo? GetUptime()
    {
        if (!OperatingSystem.IsLinux() ||
            !File.Exists("/proc/uptime") ||
            !File.Exists("/proc/loadavg"))
        {
            return null;
        }

        var uptimeText = File.ReadAllText("/proc/uptime").Trim();
        var uptimeParts = uptimeText.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (uptimeParts.Length < 1 ||
            !double.TryParse(uptimeParts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var uptimeSeconds))
        {
            return null;
        }

        var loadText = File.ReadAllText("/proc/loadavg").Trim();
        var loadParts = loadText.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (loadParts.Length < 3)
        {
            return null;
        }

        return new SystemUptimeInfo(
            TimeSpan.FromSeconds(uptimeSeconds),
            ParseDouble(loadParts[0]),
            ParseDouble(loadParts[1]),
            ParseDouble(loadParts[2]),
            DateTimeOffset.Now);
    }

    private static double ParseDouble(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0d;

    private static StorageSize? GetStorage(IReadOnlyDictionary<string, long> values, string key)
    {
        return values.TryGetValue(key, out var bytes) ? StorageSize.FromBytes(bytes) : null;
    }

    private static StorageSize? Sum(params StorageSize?[] values)
    {
        long total = 0;
        var hasValue = false;

        foreach (var value in values)
        {
            if (value is not { } size)
            {
                continue;
            }

            total += size.Bytes;
            hasValue = true;
        }

        return hasValue ? StorageSize.FromBytes(total) : null;
    }

    private static StorageSize? Subtract(StorageSize? total, params StorageSize?[] values)
    {
        if (total is not { } totalSize)
        {
            return null;
        }

        var result = totalSize.Bytes;

        foreach (var value in values)
        {
            if (value is { } size)
            {
                result -= size.Bytes;
            }
        }

        return StorageSize.FromBytes(Math.Max(0, result));
    }

    private static long ParseMemInfoBytes(string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0 ||
            !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
        {
            return 0;
        }

        var unit = parts.Length > 1 ? parts[1] : string.Empty;

        return unit.Equals("kB", StringComparison.OrdinalIgnoreCase)
            ? numeric * 1024L
            : numeric;
    }
}
