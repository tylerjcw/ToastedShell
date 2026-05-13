using Tosh.Crumb.Aur;

namespace Tosh.Crumb.Commands;

public static partial class CrumbCommands
{
    private sealed record CrumbLogEntry(
        string Path,
        string FileName,
        string Package,
        string Kind,
        DateTime LastWriteUtc,
        long Size);

    public static Task<int> LogsAsync(CrumbOptions opt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var entries = EnumerateLogs(opt.LogsPackage)
            .OrderByDescending(e => e.LastWriteUtc)
            .ToList();

        if (opt.LogsClean)
            return Task.FromResult(CleanLogs(entries, opt));

        if (opt.LogsTail)
            return Task.FromResult(TailLog(entries, opt));

        return Task.FromResult(ListLogs(entries, opt));
    }

    private static IEnumerable<CrumbLogEntry> EnumerateLogs(string? package)
    {
        var dir = AurBuilder.LogDir;
        if (!Directory.Exists(dir)) yield break;

        foreach (var path in Directory.EnumerateFiles(dir, "*.log", SearchOption.TopDirectoryOnly))
        {
            var entry = ParseLogEntry(path);
            if (package is not null && !string.Equals(entry.Package, package, StringComparison.Ordinal))
                continue;
            yield return entry;
        }
    }

    private static CrumbLogEntry ParseLogEntry(string path)
    {
        var file = Path.GetFileName(path);
        var stem = Path.GetFileNameWithoutExtension(path);
        var package = stem;
        var kind = "log";

        if (stem.Length > 16 && stem[^16] == '-' && LooksLikeStamp(stem.AsSpan(stem.Length - 15)))
        {
            var prefix = stem[..^16];
            if (prefix.EndsWith("-clone", StringComparison.Ordinal))
            {
                package = prefix[..^6];
                kind = "clone";
            }
            else
            {
                package = prefix;
                kind = "build";
            }
        }

        var info = new FileInfo(path);
        return new CrumbLogEntry(path, file, package, kind, info.LastWriteTimeUtc, info.Length);
    }

    private static bool LooksLikeStamp(ReadOnlySpan<char> s)
    {
        if (s.Length != 15 || s[8] != '-') return false;
        for (var i = 0; i < s.Length; i++)
            if (i != 8 && !char.IsAsciiDigit(s[i])) return false;
        return true;
    }

    private static int ListLogs(IReadOnlyList<CrumbLogEntry> entries, CrumbOptions opt)
    {
        if (entries.Count == 0)
        {
            Console.WriteLine($"crumb logs: no logs in {AurBuilder.LogDir}");
            return 0;
        }

        var limit = opt.Limit ?? 20;
        var shown = entries.Take(limit).ToList();
        Console.WriteLine($"crumb logs: {AurBuilder.LogDir}");
        foreach (var e in shown)
        {
            Console.WriteLine($"{e.LastWriteUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {FormatFileSize(e.Size),8}  {e.Package,-24} {e.Kind,-6} {e.FileName}");
        }
        if (entries.Count > shown.Count)
            Console.WriteLine($"crumb logs: {entries.Count - shown.Count} more (use --limit {entries.Count})");
        return 0;
    }

    private static int TailLog(IReadOnlyList<CrumbLogEntry> entries, CrumbOptions opt)
    {
        if (entries.Count == 0)
        {
            Console.WriteLine($"crumb logs: no logs in {AurBuilder.LogDir}");
            return 0;
        }

        var lines = opt.Limit ?? 80;
        var entry = entries[0];
        Console.WriteLine($"==> {entry.Path} <==");
        try
        {
            foreach (var line in File.ReadLines(entry.Path).TakeLast(lines))
                Console.WriteLine(line);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"crumb logs: cannot read '{entry.Path}': {ex.Message}");
            return 1;
        }
    }

    private static int CleanLogs(IReadOnlyList<CrumbLogEntry> entries, CrumbOptions opt)
    {
        if (entries.Count == 0)
        {
            Console.WriteLine($"crumb logs: no logs in {AurBuilder.LogDir}");
            return 0;
        }

        var victims = entries.Take(opt.Limit ?? entries.Count).ToList();
        if (opt.DryRun)
        {
            Console.WriteLine($"crumb: dry-run: would remove {victims.Count} log file(s)");
            foreach (var e in victims) Console.WriteLine($"  {e.Path}");
            return 0;
        }

        var removed = 0;
        foreach (var e in victims)
        {
            try
            {
                File.Delete(e.Path);
                removed++;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"crumb logs: failed to remove '{e.Path}': {ex.Message}");
            }
        }
        Console.WriteLine($"crumb logs: removed {removed} log file(s)");
        return removed == victims.Count ? 0 : 1;
    }
}
