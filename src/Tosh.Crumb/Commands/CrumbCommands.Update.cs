using Tosh.Crumb.Aur;
using Tosh.Crumb.Models;
using Tosh.Crumb.Output;
using Tosh.Crumb.Pacman;

namespace Tosh.Crumb.Commands;

public static partial class CrumbCommands
{
    public static async Task<int> UpdateAsync(CrumbOptions opt, CancellationToken ct)
    {
        var summary = new List<(string Phase, UpgradeListFormatter.ResultStatus Status, string Detail)>();
        // `crumb update` ⇒ `pacman -Syu` then rebuild any installed AUR
        // packages with newer upstream versions. `-Su` (no refresh) ⇒
        // upgrade against cached DBs without a fresh sync.
        if (!opt.AurOnly)
        {
            if (opt.Refresh && !opt.DryRun)
            {
                Confirm.Status(":: Synchronizing package databases (Official)...");
                var refreshArgs = new List<string> { "pacman", "-Sy", "--noconfirm" };
                var rrc = await RunEscalatedAsync(refreshArgs, dryRun: false, ct);
                if (rrc != 0) return rrc;
            }
            else if (opt.Refresh && opt.DryRun)
            {
                Console.WriteLine("crumb: dry-run: " + string.Join(' ', Privilege.Wrap(new List<string> { "pacman", "-Sy" })));
            }

            Confirm.Status(":: Starting full system upgrade...");
            var pending = await ListPendingUpgradesAsync(ct);
            if (pending.Count == 0)
            {
                Confirm.Status(" there is nothing to do");
                summary.Add(("Repo upgrades", UpgradeListFormatter.ResultStatus.Skipped, "nothing to do"));
            }
            else
            {
                var dbForRepos = new PacmanDb();
                var repoLookup = dbForRepos.Sync
                    .GroupBy(p => p.Name, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.First().Repo, StringComparer.Ordinal);
                UpgradeListFormatter.RenderRepoUpgrades(pending, repoLookup);

                if (!opt.NoConfirm && !opt.DryRun)
                {
                    if (!Confirm.YesNo(":: Proceed with upgrade?"))
                    {
                        Console.Error.WriteLine("crumb: cancelled");
                        summary.Add(("Repo upgrades", UpgradeListFormatter.ResultStatus.Skipped, "cancelled by user"));
                        UpgradeListFormatter.RenderSummary(summary);
                        return 1;
                    }
                }

                var args = new List<string> { "pacman", "-Su", "--noconfirm" };
                var rc = await RunEscalatedAsync(args, opt.DryRun, ct);
                if (rc != 0)
                {
                    summary.Add(("Repo upgrades", UpgradeListFormatter.ResultStatus.Failed, $"pacman exit {rc} ({pending.Count} pending)"));
                    if (!opt.AurOnly)
                    {
                        UpgradeListFormatter.RenderSummary(summary);
                        return rc;
                    }
                }
                else
                {
                    summary.Add(("Repo upgrades", UpgradeListFormatter.ResultStatus.Success, $"{pending.Count} package(s) upgraded"));
                }
            }
        }

        if (opt.ReposOnly)
        {
            UpgradeListFormatter.RenderSummary(summary);
            return 0;
        }

        Confirm.Status(":: Synchronizing package databases (AUR)...");
        Confirm.Status(":: Looking for AUR upgrades...");

        var db = new PacmanDb();
        var syncNames = new HashSet<string>(db.Sync.Select(p => p.Name), StringComparer.Ordinal);
        var foreign = db.Local.Values
            .Where(p => !syncNames.Contains(p.Name) && !ToshSuitePackages.Contains(p.Name))
            .Select(p => p.Name)
            .ToList();

        if (foreign.Count == 0)
        {
            summary.Add(("AUR upgrades", UpgradeListFormatter.ResultStatus.Skipped, "no foreign packages"));
            UpgradeListFormatter.RenderSummary(summary);
            return 0;
        }

        IReadOnlyList<Package> latest;
        try
        {
            using var aur = new AurClient();
            latest = await aur.InfoAsync(foreign, ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"crumb: AUR info failed: {ex.Message}");
            summary.Add(("AUR upgrades", UpgradeListFormatter.ResultStatus.Failed, $"AUR info failed: {ex.Message}"));
            UpgradeListFormatter.RenderSummary(summary);
            return 1;
        }

        // Two-pass classification, paru-style:
        //   * Stable upgrades ⇒ vercmp(installed, aur.Version) < 0
        //   * Devel upgrades  ⇒ DevelTracker says upstream HEAD moved
        // No --devel flag: the devel cache is consulted automatically.
        // Devel packages without a cache entry are silently skipped
        // (run `crumb --gendb` to seed).
        var devel = DevelTracker.Load();
        Confirm.Status(":: Looking for devel upgrades...");
        var develHits = new HashSet<string>(
            await devel.CheckUpdatesAsync(foreign, ct),
            StringComparer.Ordinal);

        var upgrades = new List<(string Name, string From, string To)>();
        var lookup = latest.ToDictionary(p => p.Name, p => p, StringComparer.Ordinal);
        foreach (var name in foreign)
        {
            if (!db.Local.TryGetValue(name, out var inst)) continue;
            if (develHits.Contains(name))
            {
                upgrades.Add((name, inst.Version, "latest-commit"));
                continue;
            }
            if (!lookup.TryGetValue(name, out var aurPkg)) continue;
            if (devel.Tracks(name)) continue;
            if (Vcs.IsVcs(name)) continue;
            if (Vercmp.IsOlder(inst.Version, aurPkg.Version))
                upgrades.Add((name, inst.Version, aurPkg.Version));
        }

        if (upgrades.Count == 0)
        {
            Confirm.Status(" there is nothing to do");
            summary.Add(("AUR upgrades", UpgradeListFormatter.ResultStatus.Skipped, "nothing to do"));
            UpgradeListFormatter.RenderSummary(summary);
            return 0;
        }

        UpgradeListFormatter.RenderAurUpgrades(upgrades);

        if (!opt.NoConfirm && !opt.DryRun)
        {
            if (!Confirm.YesNo(":: Proceed with rebuild?"))
            {
                Console.Error.WriteLine("crumb: cancelled");
                summary.Add(("AUR upgrades", UpgradeListFormatter.ResultStatus.Skipped, "cancelled by user"));
                UpgradeListFormatter.RenderSummary(summary);
                return 1;
            }
        }

        // Tell makepkg to skip its own interactive prompts (the embedded
        // pacman -U is what gets stuck on the broken-TTY [Y/n] otherwise).
        var bo = new AurBuilder.BuildOptions(
            NoConfirm: true,
            AsDeps: false,
            Clean: false,
            Quiet: opt.Quiet || !opt.Verbose);
        var anyFail = 0;

        // Optional paru-style batch review of all updated PKGBUILDs.
        var reviewAur = (opt.Review
            || string.Equals(Environment.GetEnvironmentVariable("CRUMB_REVIEW"), "1", StringComparison.Ordinal))
            && !opt.NoReview;
        if (reviewAur && !opt.DryRun)
        {
            var cloned = new List<(string Pkg, string Dir)>();
            foreach (var (pkg, _, _) in upgrades)
            {
                var dir = await AurBuilder.EnsureClonedAsync(pkg, ct);
                if (dir is not null) cloned.Add((pkg, dir));
            }
            if (!await AurBuilder.BatchReviewAsync(cloned, pagerOverride: null, opt.DiffReview, ct))
            {
                Console.Error.WriteLine("crumb: cancelled");
                return 1;
            }
        }
        var sudoLoop = upgrades.Count > 1 && !opt.DryRun
            ? await SudoLoop.StartAsync(ct)
            : null;
        var aurOk = 0;
        var aurFail = 0;
        try
        {
            foreach (var (pkg, _, _) in upgrades)
            {
                if (opt.DryRun)
                {
                    Console.WriteLine($"crumb: dry-run: would rebuild AUR '{pkg}'");
                    continue;
                }
                var rc = await AurBuilder.BuildAndInstallAsync(pkg, bo, devel, ct);
                if (rc != 0)
                {
                    Console.Error.WriteLine($"crumb: AUR rebuild failed for '{pkg}'");
                    anyFail = rc;
                    aurFail++;
                }
                else
                {
                    aurOk++;
                }
            }
        }
        finally
        {
            if (sudoLoop is not null) await sudoLoop.DisposeAsync();
        }
        devel.Save();
        if (aurFail > 0)
            summary.Add(("AUR upgrades", UpgradeListFormatter.ResultStatus.Failed, $"{aurFail} failed, {aurOk} succeeded"));
        else if (aurOk > 0)
            summary.Add(("AUR upgrades", UpgradeListFormatter.ResultStatus.Success, $"{aurOk} package(s) rebuilt"));
        else
            summary.Add(("AUR upgrades", UpgradeListFormatter.ResultStatus.Skipped, "dry-run"));
        UpgradeListFormatter.RenderSummary(summary);
        return anyFail;
    }
}
