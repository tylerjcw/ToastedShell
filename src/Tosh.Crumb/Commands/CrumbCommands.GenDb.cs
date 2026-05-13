using Tosh.Crumb.Aur;
using Tosh.Crumb.Output;
using Tosh.Crumb.Pacman;

namespace Tosh.Crumb.Commands;

public static partial class CrumbCommands
{
    /// <summary>
    /// Seed the devel-commit cache by cloning each installed foreign
    /// package and recording its upstream HEAD per git source. After
    /// this, <c>crumb -Su</c> can detect when a VCS package actually
    /// has upstream changes (paru-style) rather than rebuilding all of
    /// them blindly or skipping them all.
    /// </summary>
    public static async Task<int> GenDbAsync(CrumbOptions opt, CancellationToken ct)
    {
        var db = new PacmanDb();
        var syncNames = new HashSet<string>(db.Sync.Select(p => p.Name), StringComparer.Ordinal);
        var foreign = db.Local.Values
            .Where(p => !syncNames.Contains(p.Name) && !ToshSuitePackages.Contains(p.Name))
            .Select(p => p.Name)
            .ToList();

        if (foreign.Count == 0)
        {
            Confirm.Status(":: No foreign packages installed.");
            return 0;
        }

        Confirm.Status($":: Generating devel database for {foreign.Count} foreign package(s)...");
        var tracker = DevelTracker.Load();
        var cacheDir = AurBuilder.CacheDir;
        Directory.CreateDirectory(cacheDir);

        var recorded = 0;
        foreach (var pkg in foreign)
        {
            ct.ThrowIfCancellationRequested();
            var dir = Path.Combine(cacheDir, pkg);
            var srcinfo = Path.Combine(dir, ".SRCINFO");
            var pkgbuild = Path.Combine(dir, "PKGBUILD");

            if (!File.Exists(pkgbuild))
            {
                Confirm.Status($"  ↓ {pkg}");
                if (!await CloneShallowAsync(pkg, dir, ct)) continue;
            }
            if (!File.Exists(srcinfo) && File.Exists(pkgbuild))
            {
                if (!await GenerateSrcInfoAsync(dir, srcinfo, ct)) continue;
            }
            if (!File.Exists(srcinfo)) continue;
            try
            {
                var baseline = ExtractInstalledShortSha(db.Local[pkg].Version);
                if (await tracker.RecordAsync(pkg, srcinfo, ct, baseline))
                {
                    Confirm.Status($"  ✓ {pkg}");
                    recorded++;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"crumb: warning: gendb failed for '{pkg}': {ex.Message}");
            }
        }
        tracker.Save();
        Confirm.Status($":: Recorded {recorded} devel package(s).");
        return 0;
    }

    /// <summary>
    /// Pulls a short git hash out of a pkgver-style version. Handles
    /// both common makepkg-VCS conventions:
    /// <list type="bullet">
    ///   <item><c>.g&lt;sha&gt;</c> (the official <c>pkgver()</c>
    ///     pattern: e.g. <c>r28.g073987f-1</c>)</item>
    ///   <item><c>.r&lt;count&gt;.&lt;sha&gt;</c> with no <c>g</c>
    ///     prefix (e.g. <c>r932.42853aa-1</c>)</item>
    /// </list>
    /// Returns null when no plausible hash is present.
    /// </summary>
    private static string? ExtractInstalledShortSha(string version)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            version, @"\.g([0-9a-f]{7,40})\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;

        m = System.Text.RegularExpressions.Regex.Match(
            version, @"\br\d+\.([0-9a-f]{7,40})\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static async Task<bool> GenerateSrcInfoAsync(string dir, string srcinfoPath, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("makepkg")
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--printsrcinfo");
        try
        {
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return false;
            var stdout = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0) return false;
            await File.WriteAllTextAsync(srcinfoPath, stdout, ct);
            return true;
        }
        catch { return false; }
    }

    private static async Task<bool> CloneShallowAsync(string pkg, string dir, CancellationToken ct)
    {
        if (Directory.Exists(dir)) return true;
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("clone");
        psi.ArgumentList.Add("--depth");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add($"https://aur.archlinux.org/{pkg}.git");
        psi.ArgumentList.Add(dir);
        try
        {
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return false;
            await p.WaitForExitAsync(ct);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
