using Tosh.Crumb.Aur;
using Tosh.Crumb.Models;
using Tosh.Crumb.Output;
using Tosh.Crumb.Pacman;

namespace Tosh.Crumb.Commands;

public static partial class CrumbCommands
{
    public static async Task<int> SearchAsync(CrumbOptions opt, CancellationToken ct)
    {
        if (opt.Positional.Count == 0)
        {
            Console.Error.WriteLine("crumb search: missing query");
            return 2;
        }
        var query = string.Join(" ", opt.Positional);

        var db = new PacmanDb();
        var local = db.Local;

        var repoResults = opt.AurOnly
            ? Enumerable.Empty<Package>()
            : MatchRepo(db.Sync, opt.Positional, local);

        var aurTask = opt.ReposOnly
            ? Task.FromResult<IReadOnlyList<Package>>(Array.Empty<Package>())
            : SearchAurAsync(query, opt.SearchBy, local, ct);

        var aurResults = await aurTask;

        var combined = repoResults.Concat(aurResults);
        if (opt.InstalledOnly) combined = combined.Where(p => p.Installed);

        // --limit N: trim the combined list, prioritising installed and
        // highest-voted AUR hits. Repos come first (unsorted; alpha order
        // from the on-disk DB), AUR results are sorted by votes desc.
        if (opt.Limit is { } limit && limit >= 0)
        {
            var materialised = combined.ToList();
            var repos = materialised.Where(p => !string.Equals(p.Repo, "aur", StringComparison.Ordinal));
            var aur = materialised
                .Where(p => string.Equals(p.Repo, "aur", StringComparison.Ordinal))
                .OrderByDescending(p => p.Installed)
                .ThenByDescending(p => p.Votes ?? 0);
            combined = repos.Concat(aur).Take(limit);
        }

        return PackageFormatter.Render(combined, opt.Format, opt.Verbose);
    }

    private static IEnumerable<Package> MatchRepo(IReadOnlyList<Package> sync, IReadOnlyList<string> terms, IReadOnlyDictionary<string, Package> local)
    {
        foreach (var p in sync)
        {
            var hay1 = p.Name;
            var hay2 = p.Description ?? string.Empty;
            var ok = terms.All(t =>
                hay1.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                hay2.Contains(t, StringComparison.OrdinalIgnoreCase));
            if (!ok) continue;
            yield return AnnotateInstalled(p, local);
        }
    }

    private static async Task<IReadOnlyList<Package>> SearchAurAsync(string query, string by, IReadOnlyDictionary<string, Package> local, CancellationToken ct)
    {
        try
        {
            using var aur = new AurClient();
            var results = await aur.SearchAsync(query, by, ct);
            return results.Select(p => AnnotateInstalled(p, local)).ToList();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"crumb: warning: AUR search failed: {ex.Message}");
            return Array.Empty<Package>();
        }
    }

    public static async Task<int> InfoAsync(CrumbOptions opt, CancellationToken ct)
    {
        if (opt.Positional.Count == 0)
        {
            Console.Error.WriteLine("crumb info: missing package name");
            return 2;
        }
        var db = new PacmanDb();
        var local = db.Local;
        var hits = new List<Package>();

        if (!opt.AurOnly)
        {
            var byName = db.Sync.ToLookup(p => p.Name, StringComparer.Ordinal);
            foreach (var name in opt.Positional)
                foreach (var p in byName[name])
                    hits.Add(AnnotateInstalled(p, local));
        }

        if (!opt.ReposOnly)
        {
            var missing = opt.Positional.Where(n => !hits.Any(h => h.Name == n)).ToList();
            if (missing.Count > 0)
            {
                try
                {
                    using var aur = new AurClient();
                    var aurInfo = await aur.InfoAsync(missing, ct);
                    foreach (var p in aurInfo) hits.Add(AnnotateInstalled(p, local));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"crumb: warning: AUR info failed: {ex.Message}");
                }
            }
        }

        return PackageFormatter.Render(hits, opt.Format, verbose: opt.Verbose || PackageFormatter.Resolve(opt.Format) == OutputFormat.Table);
    }

    public static int List(CrumbOptions opt)
    {
        var db = new PacmanDb();
        var sync = db.Sync.ToLookup(p => p.Name, StringComparer.Ordinal);

        IEnumerable<Package> rows = db.Local.Values.Select(p =>
        {
            var src = sync[p.Name].FirstOrDefault();
            return src is null ? p : p with { Repo = src.Repo };
        });

        if (opt.ExplicitOnly) rows = rows.Where(p => p.InstallReason == "explicit");
        // `-Qd`, the counterpart of `-Qe`. The reason is already parsed out of the local
        // desc for `--explicit` and `--orphans`; this is the same field read the other way.
        if (opt.DepsOnly) rows = rows.Where(p => p.InstallReason == "depend");
        if (opt.ForeignOnly) rows = rows.Where(p => !sync.Contains(p.Name));
        if (opt.OrphansOnly)
        {
            rows = rows.Where(p => p.InstallReason == "depend" && !NeededPackages(db).Contains(p.Name));
        }
        if (opt.Positional.Count > 0)
        {
            var filters = opt.Positional;
            rows = rows.Where(p => filters.Any(f => p.Name.Contains(f, StringComparison.OrdinalIgnoreCase)));
        }

        return PackageFormatter.Render(rows.OrderBy(p => p.Name, StringComparer.Ordinal), opt.Format, opt.Verbose);
    }

    /// <summary>
    /// Every installed package that something else still wants — the complement of
    /// the orphan set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to be the literal <c>Depends</c> of every installed package, which
    /// over-reported orphans badly: 80 against <c>pacman -Qdt</c>'s 43 on the author's
    /// machine. Two things were missing.
    /// </para>
    /// <para>
    /// <b>A dependency may be satisfied by another name.</b> <c>lutris</c> depends on
    /// <c>p7zip</c> and <c>7zip</c> *provides* it, so <c>7zip</c> looked unwanted. Every
    /// installed package's <c>Provides</c> is now mapped back to the package offering
    /// it, so satisfying a dependency under any name counts.
    /// </para>
    /// <para>
    /// <b>An optional dependency still counts.</b> `pacman` does not call a package an
    /// orphan while something lists it in <c>OptDepends</c>, and those are written
    /// <c>name: description</c> — so they need the description stripped before the name
    /// is any use.
    /// </para>
    /// <para>
    /// Provides are matched on the bare name, deliberately. A versioned provide
    /// (<c>libavcodec.so=58-64</c>) could in principle be told from another version of
    /// the same soname, and not doing so keeps at most a package or two off this list
    /// that pacman would show. That is the safe direction: this list is what someone
    /// reads before deciding what to delete, so erring towards "still wanted" costs a
    /// missed cleanup, while erring the other way costs a broken package.
    /// </para>
    /// </remarks>
    internal static HashSet<string> NeededPackages(PacmanDb db)
    {
        var providers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var p in db.Local.Values)
        {
            foreach (var provided in p.Provides)
            {
                var name = DependencyName(provided);
                if (name.Length == 0) continue;
                if (!providers.TryGetValue(name, out var list))
                    providers[name] = list = new List<string>();
                list.Add(p.Name);
            }
        }

        var needed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in db.Local.Values)
        {
            foreach (var dep in p.Depends.Concat(p.OptDepends))
            {
                var name = DependencyName(dep);
                if (name.Length == 0) continue;
                needed.Add(name);
                if (providers.TryGetValue(name, out var offering))
                    foreach (var provider in offering) needed.Add(provider);
            }
        }
        return needed;
    }

    public static int Files(CrumbOptions opt)
    {
        if (opt.Positional.Count == 0)
        {
            Console.Error.WriteLine("crumb files: missing package name");
            return 2;
        }
        var localRoot = Path.Combine(PacmanDb.DefaultRoot, "local");
        var rc = 0;
        foreach (var name in opt.Positional)
        {
            var dir = FindLocalDir(localRoot, name);
            if (dir is null)
            {
                Console.Error.WriteLine($"crumb: {name}: not installed");
                rc = 1;
                continue;
            }
            var filesPath = Path.Combine(dir, "files");
            if (!File.Exists(filesPath))
            {
                Console.Error.WriteLine($"crumb: {name}: no files manifest");
                rc = 1;
                continue;
            }
            var inSection = false;
            foreach (var line in File.ReadLines(filesPath))
            {
                if (line.Length == 0) { inSection = false; continue; }
                if (line == "%FILES%") { inSection = true; continue; }
                if (line.StartsWith('%')) { inSection = false; continue; }
                if (inSection) Console.WriteLine($"{name} /{line}");
            }
        }
        return rc;
    }

    public static int Owns(CrumbOptions opt)
    {
        if (opt.Positional.Count == 0)
        {
            Console.Error.WriteLine("crumb owns: missing path");
            return 2;
        }
        // For non-trivial path resolution (symlinks, relative paths) we
        // delegate to pacman -Qo, which is fast and authoritative.
        var psi = new System.Diagnostics.ProcessStartInfo("pacman")
        {
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        psi.ArgumentList.Add("-Qo");
        foreach (var p in opt.Positional) psi.ArgumentList.Add(p);
        try
        {
            using var proc = System.Diagnostics.Process.Start(psi);
            proc!.WaitForExit();
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"crumb owns: cannot exec pacman: {ex.Message}");
            return 1;
        }
    }
}
