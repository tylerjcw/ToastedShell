using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Tosh.Crumb.Models;

namespace Tosh.Crumb.Pacman;

/// <summary>
/// Reads the on-disk pacman database directly. This is what makes search
/// instant: we parse /var/lib/pacman/sync/*.db (gzipped tar files of
/// "desc" entries) once and keep everything in memory for the run.
///
/// The local DB (/var/lib/pacman/local/) is a plain directory tree of
/// "desc" files — same key/value format, no tar wrapper.
/// </summary>
public sealed class PacmanDb
{
    public const string DefaultRoot = "/var/lib/pacman";

    private readonly string _root;
    private List<Package>? _syncCache;
    private Dictionary<string, Package>? _localCache;

    public PacmanDb(string? root = null) => _root = root ?? DefaultRoot;

    public string SyncDir => Path.Combine(_root, "sync");
    public string LocalDir => Path.Combine(_root, "local");

    /// <summary>All packages from every sync repo. Lazily loaded.</summary>
    public IReadOnlyList<Package> Sync => _syncCache ??= LoadSync();

    /// <summary>Installed packages by name. Lazily loaded.</summary>
    public IReadOnlyDictionary<string, Package> Local => _localCache ??= LoadLocal();

    private List<Package> LoadSync()
    {
        var result = new List<Package>(capacity: 16_000);
        if (!Directory.Exists(SyncDir)) return result;

        foreach (var dbPath in Directory.EnumerateFiles(SyncDir, "*.db"))
        {
            var repo = Path.GetFileNameWithoutExtension(dbPath);
            try
            {
                foreach (var pkg in ReadSyncDb(dbPath, repo))
                    result.Add(pkg);
            }
            catch (Exception ex)
            {
                // Fall back to `pacman` for repos we can't decode (e.g.
                // zstd-with-no-magic, or other compression we don't ship).
                if (!TryFallbackPacmanSync(repo, result))
                    Console.Error.WriteLine($"crumb: warning: cannot read {dbPath}: {ex.Message}");
            }
        }
        return result;
    }

    private static IEnumerable<Package> ReadSyncDb(string dbPath, string repo)
    {
        using var fs = File.OpenRead(dbPath);
        using var decompressed = OpenDecompressedDb(fs);
        using var tar = new TarReader(decompressed);

        // Buffer one package's "desc" body at a time. Each tar entry is a
        // directory like "foo-1.0-1/" followed by "foo-1.0-1/desc".
        while (tar.GetNextEntry() is { } entry)
        {
            if (!entry.Name.EndsWith("/desc", StringComparison.Ordinal)) continue;
            if (entry.DataStream is null) continue;

            using var sr = new StreamReader(entry.DataStream, Encoding.UTF8);
            var fields = ParseDescStream(sr);
            if (fields.Count == 0) continue;
            yield return BuildPackage(fields, repo, installed: false);
        }
    }

    private Dictionary<string, Package> LoadLocal()
    {
        var dict = new Dictionary<string, Package>(StringComparer.Ordinal);
        if (!Directory.Exists(LocalDir)) return dict;

        foreach (var dir in Directory.EnumerateDirectories(LocalDir))
        {
            var desc = Path.Combine(dir, "desc");
            var files = Path.Combine(dir, "files");
            if (!File.Exists(desc)) continue;
            try
            {
                using var sr = new StreamReader(desc, Encoding.UTF8);
                var fields = ParseDescStream(sr);
                if (fields.Count == 0) continue;

                // The local desc carries a REASON key (0=explicit, 1=depend).
                var pkg = BuildPackage(fields, repo: GuessRepo(fields), installed: true);
                _ = files; // (file list reserved for future `crumb files` impl)
                dict[pkg.Name] = pkg;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"crumb: warning: cannot read {desc}: {ex.Message}");
            }
        }
        return dict;
    }

    /// <summary>The local desc doesn't always store the source repo; fall
    /// back to "local" when unknown. We backfill the real repo later by
    /// looking up the package name in the sync cache.</summary>
    private static string GuessRepo(Dictionary<string, List<string>> fields)
        => fields.TryGetValue("REPOSITORY", out var v) && v.Count > 0 ? v[0] : "local";

    /// <summary>
    /// Parse a pacman desc file. Format:
    ///   %KEY%
    ///   value
    ///   [value...]
    ///   (blank line)
    ///   %NEXT%
    ///   ...
    /// </summary>
    private static Dictionary<string, List<string>> ParseDescStream(TextReader reader)
    {
        var fields = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        string? currentKey = null;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) { currentKey = null; continue; }
            if (line.Length >= 3 && line[0] == '%' && line[^1] == '%')
            {
                currentKey = line[1..^1];
                if (!fields.ContainsKey(currentKey)) fields[currentKey] = new List<string>();
                continue;
            }
            if (currentKey is null) continue;
            fields[currentKey].Add(line);
        }
        return fields;
    }

    private static Package BuildPackage(Dictionary<string, List<string>> f, string repo, bool installed)
    {
        string? First(string k) => f.TryGetValue(k, out var v) && v.Count > 0 ? v[0] : null;
        IReadOnlyList<string> Many(string k) => f.TryGetValue(k, out var v) ? v : Array.Empty<string>();
        long? Long(string k) => long.TryParse(First(k), out var n) ? n : null;
        DateTimeOffset? Unix(string k)
            => long.TryParse(First(k), out var n) && n > 0
                ? DateTimeOffset.FromUnixTimeSeconds(n)
                : null;

        var name = First("NAME") ?? string.Empty;
        var version = First("VERSION") ?? string.Empty;

        // pacman stores REASON only for non-default values. Missing tag on
        // an *installed* package means it was installed explicitly; on a
        // sync-DB row REASON isn't meaningful, so leave it null.
        string? reason = null;
        if (installed)
            reason = First("REASON") is "1" ? "depend" : "explicit";

        return new Package
        {
            Name = name,
            Version = version,
            Repo = repo,
            Description = First("DESC"),
            Url = First("URL"),
            Packager = First("PACKAGER"),
            Architecture = First("ARCH"),
            License = First("LICENSE"),
            Base = First("BASE"),
            DownloadSize = Long("CSIZE"),
            InstalledSize = Long("ISIZE"),
            Groups = Many("GROUPS"),
            Depends = Many("DEPENDS"),
            MakeDepends = Many("MAKEDEPENDS"),
            CheckDepends = Many("CHECKDEPENDS"),
            OptDepends = Many("OPTDEPENDS"),
            Provides = Many("PROVIDES"),
            Conflicts = Many("CONFLICTS"),
            Replaces = Many("REPLACES"),
            BuildDate = Unix("BUILDDATE"),
            InstallDate = Unix("INSTALLDATE"),
            Installed = installed,
            InstalledVersion = installed ? version : null,
            InstallReason = reason,
        };
    }

    // ─── compression detection + pacman fallback ──────────────────

    /// <summary>
    /// Pacman sync DBs are tar archives wrapped in gzip, zstd, xz, or bzip2
    /// depending on the repo's <c>CompressionType</c>. Detect by magic
    /// bytes and route to the matching decompressor. zstd is the modern
    /// default; we shell out to the system `zstd` binary because .NET 10
    /// has no built-in ZstandardStream.
    /// </summary>
    private static Stream OpenDecompressedDb(FileStream fs)
    {
        Span<byte> magic = stackalloc byte[6];
        var read = fs.Read(magic);
        fs.Seek(0, SeekOrigin.Begin);

        if (read >= 2 && magic[0] == 0x1F && magic[1] == 0x8B)
            return new GZipStream(fs, CompressionMode.Decompress, leaveOpen: false);
        if (read >= 4 && magic[0] == 0x28 && magic[1] == 0xB5 && magic[2] == 0x2F && magic[3] == 0xFD)
            return SpawnDecompressor("zstd", "-dcq", fs);
        if (read >= 6 && magic[0] == 0xFD && magic[1] == 0x37 && magic[2] == 0x7A && magic[3] == 0x58 && magic[4] == 0x5A && magic[5] == 0x00)
            return SpawnDecompressor("xz", "-dcq", fs);
        if (read >= 3 && magic[0] == 0x42 && magic[1] == 0x5A && magic[2] == 0x68)
            return SpawnDecompressor("bzip2", "-dcq", fs);

        // Unknown: assume tar without compression.
        return fs;
    }

    private static Stream SpawnDecompressor(string program, string args, FileStream input)
    {
        var psi = new ProcessStartInfo(program, args)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        var proc = Process.Start(psi) ?? throw new InvalidOperationException($"failed to launch {program}");
        _ = Task.Run(async () =>
        {
            try { await input.CopyToAsync(proc.StandardInput.BaseStream); }
            finally { proc.StandardInput.Close(); }
        });
        return proc.StandardOutput.BaseStream;
    }

    /// <summary>
    /// Last-ditch fallback: parse `pacman -Sl &lt;repo&gt;` line-by-line when
    /// we can't decode the raw DB. Less info but enough for listings/search.
    /// </summary>
    private bool TryFallbackPacmanSync(string repo, List<Package> sink)
    {
        try
        {
            var psi = new ProcessStartInfo("pacman", $"-Sl {repo}")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            string? line;
            while ((line = proc.StandardOutput.ReadLine()) is not null)
            {
                var parts = line.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;
                sink.Add(new Package { Name = parts[1], Version = parts[2], Repo = repo });
            }
            proc.WaitForExit();
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }
}
