using Tosh.Crumb.Aur;
using Tosh.Crumb.Output;
using Tosh.Crumb.Pacman;

namespace Tosh.Crumb.Commands;

public static partial class CrumbCommands
{
    private sealed record CrumbInstallPlan(
        PacmanDb Db,
        List<string> RepoPkgs,
        List<string> AurPkgs,
        ResolvedPlan? AurPlan,
        IReadOnlyDictionary<string, string> RepoVersions);

    private sealed record InstallPlanningResult(CrumbInstallPlan? Plan, int ExitCode);

    public static async Task<int> InstallAsync(CrumbOptions opt, CancellationToken ct)
    {
        var summary = new List<(string Phase, UpgradeListFormatter.ResultStatus Status, string Detail)>();
        var planned = await ValidateAndPlanInstallAsync(opt, summary, ct);
        if (planned.Plan is null) return planned.ExitCode;

        if (!await RenderAndConfirmInstallPlanAsync(planned.Plan, opt, summary, ct))
            return 1;

        var repoRc = await ExecuteRepoInstallAsync(planned.Plan, opt, summary, ct);
        if (repoRc != 0) return repoRc;

        var aurRc = await ExecuteAurBuildsAsync(planned.Plan, opt, summary, ct);
        UpgradeListFormatter.RenderSummary(summary);
        return aurRc;
    }

    private static async Task<InstallPlanningResult> ValidateAndPlanInstallAsync(
        CrumbOptions opt,
        List<(string Phase, UpgradeListFormatter.ResultStatus Status, string Detail)> summary,
        CancellationToken ct)
    {
        if (opt.Positional.Count == 0 && !opt.Upgrade && (!opt.Refresh || opt.DownloadOnly))
        {
            Console.Error.WriteLine(opt.DownloadOnly
                ? "crumb install: missing package name for download"
                : "crumb install: missing package name");
            return new InstallPlanningResult(null, 2);
        }

        var db = new PacmanDb();
        var inRepo = new HashSet<string>(db.Sync.Select(p => p.Name), StringComparer.Ordinal);
        var groupMembers = BuildGroupMembers(db);
        var repoVersions = BuildRepoVersions(db);
        var expanded = ExpandInstallTargets(opt.Positional, inRepo, groupMembers);
        var (repoPkgs, aurPkgs) = PartitionInstallTargets(expanded, inRepo, opt);

        var aurPlan = await ResolveAurPlanAsync(db, aurPkgs, repoPkgs, opt, summary, ct);
        if (aurPlan.ExitCode != 0) return new InstallPlanningResult(null, aurPlan.ExitCode);

        return new InstallPlanningResult(
            new CrumbInstallPlan(db, repoPkgs, aurPkgs, aurPlan.Plan, repoVersions),
            0);
    }

    private static Dictionary<string, List<string>> BuildGroupMembers(PacmanDb db)
    {
        var groupMembers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var p in db.Sync)
        {
            foreach (var g in p.Groups)
            {
                if (!groupMembers.TryGetValue(g, out var list))
                    groupMembers[g] = list = new List<string>();
                list.Add(p.Name);
            }
        }
        return groupMembers;
    }

    private static Dictionary<string, string> BuildRepoVersions(PacmanDb db)
    {
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in db.Sync)
            versions.TryAdd(p.Name, p.Version);
        return versions;
    }

    private static List<string> ExpandInstallTargets(
        IReadOnlyList<string> targets,
        HashSet<string> inRepo,
        IReadOnlyDictionary<string, List<string>> groupMembers)
    {
        var expanded = new List<string>(targets.Count);
        foreach (var name in targets)
        {
            if (!inRepo.Contains(name) && groupMembers.TryGetValue(name, out var members) && members.Count > 0)
            {
                Confirm.Status($":: '{name}' is a group — expanding to {members.Count} member(s)");
                expanded.AddRange(members);
            }
            else
            {
                expanded.Add(name);
            }
        }
        return expanded;
    }

    private static (List<string> RepoPkgs, List<string> AurPkgs) PartitionInstallTargets(
        IEnumerable<string> expanded,
        HashSet<string> inRepo,
        CrumbOptions opt)
    {
        var repoPkgs = new List<string>();
        var aurPkgs = new List<string>();
        foreach (var name in expanded)
        {
            if (opt.AurOnly) { aurPkgs.Add(name); continue; }
            if (opt.ReposOnly) { repoPkgs.Add(name); continue; }
            (inRepo.Contains(name) ? repoPkgs : aurPkgs).Add(name);
        }
        return (repoPkgs, aurPkgs);
    }

    private sealed record AurPlanResult(ResolvedPlan? Plan, int ExitCode);

    private static async Task<AurPlanResult> ResolveAurPlanAsync(
        PacmanDb db,
        List<string> aurPkgs,
        List<string> repoPkgs,
        CrumbOptions opt,
        List<(string Phase, UpgradeListFormatter.ResultStatus Status, string Detail)> summary,
        CancellationToken ct)
    {
        if (aurPkgs.Count == 0) return new AurPlanResult(null, 0);

        using var aurClient = new AurClient();
        var resolver = new DependencyResolver(db, aurClient, needed: opt.Needed);
        var plan = await resolver.ResolveAsync(aurPkgs, ct);

        if (plan.Missing.Count > 0)
        {
            Console.Error.WriteLine($"crumb: error: cannot find {string.Join(", ", plan.Missing)}");
            summary.Add(("Install", UpgradeListFormatter.ResultStatus.Failed, $"missing: {string.Join(", ", plan.Missing)}"));
            UpgradeListFormatter.RenderSummary(summary);
            return new AurPlanResult(null, 1);
        }

        foreach (var r in plan.RepoTargets)
            if (!repoPkgs.Contains(r, StringComparer.Ordinal)) repoPkgs.Add(r);
        aurPkgs.Clear();
        aurPkgs.AddRange(plan.AurBuilds);

        if (plan.Conflicts.Count > 0)
        {
            Confirm.Status("crumb: conflicts detected:");
            foreach (var (pkg, with) in plan.Conflicts)
                Confirm.Status($"  {pkg} conflicts with installed {with}");
            if (!opt.NoConfirm && !opt.DryRun)
            {
                if (!Confirm.YesNo("Proceed anyway?", defaultYes: false))
                {
                    Console.Error.WriteLine("crumb: cancelled");
                    summary.Add(("Install", UpgradeListFormatter.ResultStatus.Skipped, "cancelled by user (conflicts)"));
                    UpgradeListFormatter.RenderSummary(summary);
                    return new AurPlanResult(null, 1);
                }
            }
        }

        return new AurPlanResult(plan, 0);
    }

    private static async Task<bool> RenderAndConfirmInstallPlanAsync(
        CrumbInstallPlan plan,
        CrumbOptions opt,
        List<(string Phase, UpgradeListFormatter.ResultStatus Status, string Detail)> summary,
        CancellationToken ct)
    {
        var hasRepoAction = plan.RepoPkgs.Count > 0 || opt.Refresh || opt.Upgrade;
        if (hasRepoAction)
        {
            var rows = BuildInstallPlanRows(plan);
            if (rows.Count > 0)
                UpgradeListFormatter.RenderPlan(rows, opt.DownloadOnly ? "Packages to download" : "Packages to install", opt.GroupBy);

            ReportSkippedAurTargets(plan.AurPlan);

            if (opt.Upgrade)
            {
                var pending = await ListPendingUpgradesAsync(ct);
                if (pending.Count > 0)
                    UpgradeListFormatter.RenderRepoUpgrades(pending, BuildRepoLookup(plan.Db));
            }

            if (!opt.NoConfirm && !opt.DryRun && (plan.RepoPkgs.Count > 0 || opt.Upgrade))
            {
                if (!Confirm.YesNo(opt.DownloadOnly ? "Download packages?" : "Proceed?"))
                {
                    Console.Error.WriteLine("crumb: cancelled");
                    summary.Add((opt.DownloadOnly ? "Repo download" : "Repo install",
                        UpgradeListFormatter.ResultStatus.Skipped,
                        "cancelled by user"));
                    UpgradeListFormatter.RenderSummary(summary);
                    return false;
                }
            }
        }
        else if (plan.AurPkgs.Count > 0 && plan.AurPlan is not null)
        {
            var title = opt.DownloadOnly ? "AUR PKGBUILDs to fetch" : "AUR packages to build";
            var aurRows = plan.AurPlan.AurBuilds
                .Select(n => ("aur", n, plan.AurPlan.AurVersions.TryGetValue(n, out var v) ? v : string.Empty))
                .ToList();
            if (aurRows.Count > 0)
                UpgradeListFormatter.RenderPlan(aurRows, title, opt.GroupBy);

            ReportSkippedAurTargets(plan.AurPlan);

            if (!opt.NoConfirm && !opt.DryRun && aurRows.Count > 0)
            {
                if (!Confirm.YesNo(opt.DownloadOnly ? "Fetch PKGBUILDs?" : "Proceed?"))
                {
                    Console.Error.WriteLine("crumb: cancelled");
                    summary.Add((opt.DownloadOnly ? "AUR download" : "AUR build",
                        UpgradeListFormatter.ResultStatus.Skipped,
                        "cancelled by user"));
                    UpgradeListFormatter.RenderSummary(summary);
                    return false;
                }
            }
        }

        return true;
    }

    private static List<(string Source, string Name, string Version)> BuildInstallPlanRows(CrumbInstallPlan plan)
    {
        var repoByName = BuildRepoLookup(plan.Db);
        var rows = new List<(string Source, string Name, string Version)>();
        foreach (var name in plan.RepoPkgs)
        {
            var source = repoByName.TryGetValue(name, out var r) ? r : "sync";
            var version = plan.RepoVersions.TryGetValue(name, out var v) ? v : string.Empty;
            rows.Add((source, name, version));
        }
        if (plan.AurPlan is not null)
        {
            foreach (var name in plan.AurPlan.AurBuilds)
            {
                var version = plan.AurPlan.AurVersions.TryGetValue(name, out var v) ? v : string.Empty;
                rows.Add(("aur", name, version));
            }
        }
        return rows;
    }

    private static Dictionary<string, string> BuildRepoLookup(PacmanDb db)
    {
        return db.Sync
            .GroupBy(p => p.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Repo, StringComparer.Ordinal);
    }

    private static void ReportSkippedAurTargets(ResolvedPlan? plan)
    {
        if (plan is null || plan.Skipped.Count == 0) return;
        foreach (var (p, why) in plan.Skipped)
            Confirm.Status($"crumb: skip {p}: {why}");
    }

    private static async Task<int> ExecuteRepoInstallAsync(
        CrumbInstallPlan plan,
        CrumbOptions opt,
        List<(string Phase, UpgradeListFormatter.ResultStatus Status, string Detail)> summary,
        CancellationToken ct)
    {
        if (plan.RepoPkgs.Count == 0 && !opt.Refresh && !opt.Upgrade) return 0;

        var args = new List<string> { "pacman", BuildSyncOperation(opt), "--noconfirm" };
        if (opt.Needed) args.Add("--needed");
        var aurRepoDeps = plan.AurPlan?.RepoTargets ?? Array.Empty<string>();
        var userRepoOnly = plan.RepoPkgs.Where(r => !aurRepoDeps.Contains(r, StringComparer.Ordinal)).ToList();
        var phase = opt.DownloadOnly ? "Repo download" : "Repo install";
        var verb = opt.DownloadOnly ? "downloaded" : "installed";

        if (aurRepoDeps.Count > 0)
        {
            var depArgs = new List<string>(args);
            if (!opt.DownloadOnly) depArgs.Add("--asdeps");
            depArgs.AddRange(aurRepoDeps);
            var rcDeps = await RunEscalatedAsync(depArgs, opt.DryRun, ct);
            if (rcDeps != 0)
            {
                summary.Add((phase, UpgradeListFormatter.ResultStatus.Failed, $"pacman exit {rcDeps} (deps)"));
                UpgradeListFormatter.RenderSummary(summary);
                return rcDeps;
            }

            if (userRepoOnly.Count > 0)
            {
                var userArgs = new List<string>(args);
                if (opt.AsDeps && !opt.DownloadOnly) userArgs.Add("--asdeps");
                userArgs.AddRange(userRepoOnly);
                var rcUser = await RunEscalatedAsync(userArgs, opt.DryRun, ct);
                if (rcUser != 0 && !opt.Upgrade)
                {
                    summary.Add((phase, UpgradeListFormatter.ResultStatus.Failed, $"pacman exit {rcUser}"));
                    UpgradeListFormatter.RenderSummary(summary);
                    return rcUser;
                }
            }

            summary.Add((phase, UpgradeListFormatter.ResultStatus.Success, $"{aurRepoDeps.Count + userRepoOnly.Count} package(s) {verb}"));
            return 0;
        }

        if (plan.RepoPkgs.Count > 0 || opt.Upgrade || opt.Refresh)
        {
            if (opt.AsDeps && !opt.DownloadOnly) args.Add("--asdeps");
            args.AddRange(plan.RepoPkgs);
            var rc = await RunEscalatedAsync(args, opt.DryRun, ct);
            if (rc != 0 && !opt.Upgrade)
            {
                summary.Add((phase, UpgradeListFormatter.ResultStatus.Failed, $"pacman exit {rc}"));
                UpgradeListFormatter.RenderSummary(summary);
                return rc;
            }
            if (plan.RepoPkgs.Count > 0)
                summary.Add((phase, UpgradeListFormatter.ResultStatus.Success, $"{plan.RepoPkgs.Count} package(s) {verb}"));
        }

        return 0;
    }

    private static string BuildSyncOperation(CrumbOptions opt)
    {
        var flag = "-S";
        if (opt.Refresh) flag += "y";
        if (opt.Upgrade) flag += "u";
        if (opt.DownloadOnly) flag += "w";
        return flag;
    }

    private static bool ShouldReviewAur(CrumbOptions opt)
    {
        return (opt.Review
            || string.Equals(Environment.GetEnvironmentVariable("CRUMB_REVIEW"), "1", StringComparison.Ordinal))
            && !opt.NoReview;
    }

    private static async Task<int> ExecuteAurBuildsAsync(
        CrumbInstallPlan plan,
        CrumbOptions opt,
        List<(string Phase, UpgradeListFormatter.ResultStatus Status, string Detail)> summary,
        CancellationToken ct)
    {
        if (plan.AurPkgs.Count == 0) return 0;

        var reviewAur = ShouldReviewAur(opt);
        var clonedTargets = await FetchAurTargetsAsync(plan.AurPkgs, opt, reviewAur, summary, ct);
        if (!opt.DryRun && clonedTargets is null) return 1;

        if (opt.DownloadOnly)
        {
            if (opt.DryRun)
            {
                foreach (var pkg in plan.AurPkgs)
                    Confirm.Status($"crumb: dry-run: would fetch AUR PKGBUILD '{pkg}'" + (reviewAur ? " (with review)" : ""));
            }
            else
            {
                summary.Add(("AUR download", UpgradeListFormatter.ResultStatus.Success, $"{plan.AurPkgs.Count} PKGBUILD(s) fetched"));
            }
            return 0;
        }

        var develTracker = DevelTracker.Load();
        var sudoLoop = (opt.SudoLoop || plan.AurPkgs.Count > 1) && !opt.DryRun
            ? await SudoLoop.StartAsync(ct)
            : null;
        var aurRc = 0;
        var aurOk = 0;
        var aurFail = 0;
        try
        {
            foreach (var pkg in plan.AurPkgs)
            {
                if (opt.DryRun)
                {
                    Confirm.Status($"crumb: dry-run: would build+install AUR '{pkg}'" + (reviewAur ? " (with PKGBUILD review)" : ""));
                    continue;
                }
                var bo = new AurBuilder.BuildOptions(
                    NoConfirm: true,
                    AsDeps: opt.AsDeps,
                    Clean: false,
                    Quiet: opt.Quiet || !opt.Verbose);
                var rc = await AurBuilder.BuildAndInstallAsync(pkg, bo, develTracker, ct);
                if (rc != 0)
                {
                    Console.Error.WriteLine($"crumb: AUR build failed for '{pkg}' (exit {rc})");
                    aurRc = rc;
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

        develTracker.Save();
        if (!opt.DryRun)
        {
            if (aurFail > 0)
                summary.Add(("AUR build", UpgradeListFormatter.ResultStatus.Failed, $"{aurFail} failed, {aurOk} succeeded"));
            else if (aurOk > 0)
                summary.Add(("AUR build", UpgradeListFormatter.ResultStatus.Success, $"{aurOk} package(s) built"));
        }
        return aurRc;
    }

    private static async Task<List<(string Pkg, string Dir)>?> FetchAurTargetsAsync(
        IReadOnlyList<string> aurPkgs,
        CrumbOptions opt,
        bool reviewAur,
        List<(string Phase, UpgradeListFormatter.ResultStatus Status, string Detail)> summary,
        CancellationToken ct)
    {
        if (aurPkgs.Count == 0 || opt.DryRun) return new List<(string, string)>();

        Confirm.Status($":: Fetching {aurPkgs.Count} PKGBUILD(s)...");
        var clonedTargets = new List<(string Pkg, string Dir)>();
        foreach (var pkg in aurPkgs)
        {
            var dir = await AurBuilder.EnsureClonedAsync(pkg, ct);
            if (dir is null)
            {
                Console.Error.WriteLine($"crumb: no PKGBUILD in clone of '{pkg}' — is the package name correct?");
                summary.Add((opt.DownloadOnly ? "AUR download" : "AUR build",
                    UpgradeListFormatter.ResultStatus.Failed,
                    $"clone failed: {pkg}"));
                UpgradeListFormatter.RenderSummary(summary);
                return null;
            }
            clonedTargets.Add((pkg, dir));
        }

        if (reviewAur)
        {
            if (!await AurBuilder.BatchReviewAsync(clonedTargets, pagerOverride: null, opt.DiffReview, ct))
            {
                Console.Error.WriteLine("crumb: cancelled");
                summary.Add((opt.DownloadOnly ? "AUR download" : "AUR build",
                    UpgradeListFormatter.ResultStatus.Skipped,
                    "cancelled at review"));
                UpgradeListFormatter.RenderSummary(summary);
                return null;
            }
        }

        return clonedTargets;
    }
}
