using Tosh.Crumb.Aur;
using Tosh.Crumb.Models;
using Tosh.Crumb.Output;
using Tosh.Crumb.Pacman;

namespace Tosh.Crumb.Commands;

public static partial class CrumbCommands
{
    private sealed record AurUpgradePlan(
        List<(string Name, string From, string To)> Upgrades,
        DevelTracker Devel);

    private sealed record AurUpgradePlanResult(AurUpgradePlan? Plan, int ExitCode, bool Stop);

    public static async Task<int> UpdateAsync(CrumbOptions opt, CancellationToken ct)
    {
        var summary = new List<(string Phase, UpgradeListFormatter.ResultStatus Status, string Detail)>();

        var repoRc = await UpgradeRepoAsync(opt, summary, ct);
        if (repoRc != 0) return repoRc;

        if (opt.ReposOnly)
        {
            UpgradeListFormatter.RenderSummary(summary);
            return 0;
        }

        var aurPlanResult = await FindAurUpgradePlanAsync(summary, ct);
        if (aurPlanResult.Stop) return aurPlanResult.ExitCode;
        var aurPlan = aurPlanResult.Plan!;

        UpgradeListFormatter.RenderAurUpgrades(aurPlan.Upgrades);

        if (!await ConfirmAurUpgradeAsync(opt, summary))
            return 1;

        var aurRc = opt.DownloadOnly
            ? await DownloadAurUpgradePkbuildsAsync(aurPlan, opt, summary, ct)
            : await ExecuteAurUpgradeBuildsAsync(aurPlan, opt, summary, ct);

        UpgradeListFormatter.RenderSummary(summary);
        return aurRc;
    }

    private static async Task<int> UpgradeRepoAsync(
        CrumbOptions opt,
        List<(string Phase, UpgradeListFormatter.ResultStatus Status, string Detail)> summary,
        CancellationToken ct)
    {
        if (opt.AurOnly) return 0;

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

        Confirm.Status(opt.DownloadOnly
            ? ":: Downloading full system upgrade packages..."
            : ":: Starting full system upgrade...");

        var phase = opt.DownloadOnly ? "Repo downloads" : "Repo upgrades";
        var pending = await ListPendingUpgradesAsync(ct);
        if (pending.Count == 0)
        {
            Confirm.Status(" there is nothing to do");
            summary.Add((phase, UpgradeListFormatter.ResultStatus.Skipped, "nothing to do"));
            return 0;
        }

        var dbForRepos = new PacmanDb();
        var repoLookup = dbForRepos.Sync
            .GroupBy(p => p.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Repo, StringComparer.Ordinal);
        UpgradeListFormatter.RenderRepoUpgrades(pending, repoLookup);

        if (!opt.NoConfirm && !opt.DryRun)
        {
            if (!Confirm.YesNo(opt.DownloadOnly ? ":: Download upgrade packages?" : ":: Proceed with upgrade?"))
            {
                Console.Error.WriteLine("crumb: cancelled");
                summary.Add((phase, UpgradeListFormatter.ResultStatus.Skipped, "cancelled by user"));
                UpgradeListFormatter.RenderSummary(summary);
                return 1;
            }
        }

        var args = new List<string> { "pacman", opt.DownloadOnly ? "-Suw" : "-Su", "--noconfirm" };
        var rc = await RunEscalatedAsync(args, opt.DryRun, ct);
        if (rc != 0)
        {
            summary.Add((phase, UpgradeListFormatter.ResultStatus.Failed, $"pacman exit {rc} ({pending.Count} pending)"));
            UpgradeListFormatter.RenderSummary(summary);
            return rc;
        }

        summary.Add((phase,
            UpgradeListFormatter.ResultStatus.Success,
            opt.DownloadOnly ? $"{pending.Count} package(s) downloaded" : $"{pending.Count} package(s) upgraded"));
        return 0;
    }

    private static async Task<AurUpgradePlanResult> FindAurUpgradePlanAsync(
        List<(string Phase, UpgradeListFormatter.ResultStatus Status, string Detail)> summary,
        CancellationToken ct)
    {
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
            return new AurUpgradePlanResult(null, 0, Stop: true);
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
            return new AurUpgradePlanResult(null, 1, Stop: true);
        }

        var devel = DevelTracker.Load();
        Confirm.Status(":: Looking for devel upgrades...");
        var develHits = new HashSet<string>(
            await devel.CheckUpdatesAsync(foreign, ct),
            StringComparer.Ordinal);

        var upgrades = ClassifyAurUpgrades(db, foreign, latest, devel, develHits);
        if (upgrades.Count == 0)
        {
            Confirm.Status(" there is nothing to do");
            summary.Add(("AUR upgrades", UpgradeListFormatter.ResultStatus.Skipped, "nothing to do"));
            UpgradeListFormatter.RenderSummary(summary);
            return new AurUpgradePlanResult(null, 0, Stop: true);
        }

        return new AurUpgradePlanResult(new AurUpgradePlan(upgrades, devel), 0, Stop: false);
    }

    private static List<(string Name, string From, string To)> ClassifyAurUpgrades(
        PacmanDb db,
        IReadOnlyList<string> foreign,
        IReadOnlyList<Package> latest,
        DevelTracker devel,
        HashSet<string> develHits)
    {
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
        return upgrades;
    }

    private static Task<bool> ConfirmAurUpgradeAsync(
        CrumbOptions opt,
        List<(string Phase, UpgradeListFormatter.ResultStatus Status, string Detail)> summary)
    {
        if (opt.NoConfirm || opt.DryRun) return Task.FromResult(true);

        if (Confirm.YesNo(opt.DownloadOnly ? ":: Fetch updated PKGBUILDs?" : ":: Proceed with rebuild?"))
            return Task.FromResult(true);

        Console.Error.WriteLine("crumb: cancelled");
        summary.Add(("AUR upgrades", UpgradeListFormatter.ResultStatus.Skipped, "cancelled by user"));
        UpgradeListFormatter.RenderSummary(summary);
        return Task.FromResult(false);
    }

    private static async Task<int> DownloadAurUpgradePkbuildsAsync(
        AurUpgradePlan plan,
        CrumbOptions opt,
        List<(string Phase, UpgradeListFormatter.ResultStatus Status, string Detail)> summary,
        CancellationToken ct)
    {
        var reviewAur = ShouldReviewAur(opt);
        if (opt.DryRun)
        {
            foreach (var (pkg, _, _) in plan.Upgrades)
                Console.WriteLine($"crumb: dry-run: would fetch AUR PKGBUILD '{pkg}'" + (reviewAur ? " (with review)" : ""));
            summary.Add(("AUR downloads", UpgradeListFormatter.ResultStatus.Skipped, "dry-run"));
            return 0;
        }

        var cloned = await FetchAurUpgradeTargetsAsync(plan.Upgrades, reviewAur, opt, summary, ct);
        if (cloned is null) return 1;

        summary.Add(("AUR downloads", UpgradeListFormatter.ResultStatus.Success, $"{cloned.Count} PKGBUILD(s) fetched"));
        return 0;
    }

    private static async Task<int> ExecuteAurUpgradeBuildsAsync(
        AurUpgradePlan plan,
        CrumbOptions opt,
        List<(string Phase, UpgradeListFormatter.ResultStatus Status, string Detail)> summary,
        CancellationToken ct)
    {
        if (!await ReviewAurUpgradesAsync(plan.Upgrades, opt, summary, ct))
            return 1;

        var bo = new AurBuilder.BuildOptions(
            NoConfirm: true,
            AsDeps: false,
            Clean: false,
            Quiet: opt.Quiet || !opt.Verbose);
        var anyFail = 0;
        var sudoLoop = plan.Upgrades.Count > 1 && !opt.DryRun
            ? await SudoLoop.StartAsync(ct)
            : null;
        var aurOk = 0;
        var aurFail = 0;
        try
        {
            foreach (var (pkg, _, _) in plan.Upgrades)
            {
                if (opt.DryRun)
                {
                    Console.WriteLine($"crumb: dry-run: would rebuild AUR '{pkg}'");
                    continue;
                }
                var rc = await AurBuilder.BuildAndInstallAsync(pkg, bo, plan.Devel, ct);
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

        plan.Devel.Save();
        if (aurFail > 0)
            summary.Add(("AUR upgrades", UpgradeListFormatter.ResultStatus.Failed, $"{aurFail} failed, {aurOk} succeeded"));
        else if (aurOk > 0)
            summary.Add(("AUR upgrades", UpgradeListFormatter.ResultStatus.Success, $"{aurOk} package(s) rebuilt"));
        else
            summary.Add(("AUR upgrades", UpgradeListFormatter.ResultStatus.Skipped, "dry-run"));
        return anyFail;
    }

    private static async Task<bool> ReviewAurUpgradesAsync(
        IReadOnlyList<(string Name, string From, string To)> upgrades,
        CrumbOptions opt,
        List<(string Phase, UpgradeListFormatter.ResultStatus Status, string Detail)> summary,
        CancellationToken ct)
    {
        var reviewAur = ShouldReviewAur(opt);
        if (!reviewAur || opt.DryRun) return true;

        return await FetchAurUpgradeTargetsAsync(upgrades, reviewAur, opt, summary, ct) is not null;
    }

    private static async Task<List<(string Pkg, string Dir)>?> FetchAurUpgradeTargetsAsync(
        IReadOnlyList<(string Name, string From, string To)> upgrades,
        bool reviewAur,
        CrumbOptions opt,
        List<(string Phase, UpgradeListFormatter.ResultStatus Status, string Detail)> summary,
        CancellationToken ct)
    {
        var cloned = new List<(string Pkg, string Dir)>();
        foreach (var (pkg, _, _) in upgrades)
        {
            var dir = await AurBuilder.EnsureClonedAsync(pkg, ct);
            if (dir is not null) cloned.Add((pkg, dir));
            else
            {
                Console.Error.WriteLine($"crumb: no PKGBUILD in clone of '{pkg}' — is the package name correct?");
                summary.Add((opt.DownloadOnly ? "AUR downloads" : "AUR upgrades",
                    UpgradeListFormatter.ResultStatus.Failed,
                    $"clone failed: {pkg}"));
                UpgradeListFormatter.RenderSummary(summary);
                return null;
            }
        }

        if (reviewAur && !await AurBuilder.BatchReviewAsync(cloned, pagerOverride: null, opt.DiffReview, ct))
        {
            Console.Error.WriteLine("crumb: cancelled");
            summary.Add((opt.DownloadOnly ? "AUR downloads" : "AUR upgrades",
                UpgradeListFormatter.ResultStatus.Skipped,
                "cancelled at review"));
            UpgradeListFormatter.RenderSummary(summary);
            return null;
        }

        return cloned;
    }
}
