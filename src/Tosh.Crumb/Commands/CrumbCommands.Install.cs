using Tosh.Crumb.Aur;
using Tosh.Crumb.Output;
using Tosh.Crumb.Pacman;

namespace Tosh.Crumb.Commands;

public static partial class CrumbCommands
{
    public static async Task<int> InstallAsync(CrumbOptions opt, CancellationToken ct)
    {
        if (opt.Positional.Count == 0 && !opt.Upgrade && !opt.Refresh)
        {
            Console.Error.WriteLine("crumb install: missing package name");
            return 2;
        }

        var summary = new List<(string Phase, UpgradeListFormatter.ResultStatus Status, string Detail)>();
        var db = new PacmanDb();
        var inRepo = new HashSet<string>(db.Sync.Select(p => p.Name), StringComparer.Ordinal);

        // Build group→members map: any positional that matches a group
        // name (and isn't also a package) gets expanded to its members.
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

        var repoVersions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in db.Sync)
            repoVersions.TryAdd(p.Name, p.Version);

        // Expand group names into their members before the repo/AUR
        // split. Mirrors `pacman -S <group>`: package names take
        // precedence over group names, so we only expand when the
        // positional is not itself a known package.
        var expanded = new List<string>(opt.Positional.Count);
        foreach (var name in opt.Positional)
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

        var repoPkgs = new List<string>();
        var aurPkgs = new List<string>();
        foreach (var name in expanded)
        {
            if (opt.AurOnly) { aurPkgs.Add(name); continue; }
            if (opt.ReposOnly) { repoPkgs.Add(name); continue; }
            (inRepo.Contains(name) ? repoPkgs : aurPkgs).Add(name);
        }

        // Resolve transitive AUR deps up front. The resolver also
        // pulls any repo deps of AUR builds out so we can hand them
        // to pacman in one shot and have everything ready before
        // makepkg starts running build hooks.
        ResolvedPlan? plan = null;
        if (aurPkgs.Count > 0)
        {
            using var aurClient = new AurClient();
            var resolver = new DependencyResolver(db, aurClient, needed: opt.Needed);
            plan = await resolver.ResolveAsync(aurPkgs, ct);

            if (plan.Missing.Count > 0)
            {
                Console.Error.WriteLine($"crumb: error: cannot find {string.Join(", ", plan.Missing)}");
                summary.Add(("Install", UpgradeListFormatter.ResultStatus.Failed, $"missing: {string.Join(", ", plan.Missing)}"));
                UpgradeListFormatter.RenderSummary(summary);
                return 1;
            }
            foreach (var r in plan.RepoTargets)
                if (!repoPkgs.Contains(r)) repoPkgs.Add(r);
            aurPkgs = plan.AurBuilds.ToList();

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
                        return 1;
                    }
                }
            }
        }

        // Handle a bare `crumb -Syu` (no positionals) with --upgrade as
        // repo-only sysupgrade plus a follow-up AUR refresh.
        if (repoPkgs.Count > 0 || opt.Refresh || opt.Upgrade)
        {
            var repoByName = db.Sync
                .GroupBy(p => p.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Repo, StringComparer.Ordinal);

            var planRows = new List<(string Source, string Name, string Version)>();
            foreach (var name in repoPkgs)
            {
                var source = repoByName.TryGetValue(name, out var r) ? r : "sync";
                var version = repoVersions.TryGetValue(name, out var v) ? v : string.Empty;
                planRows.Add((source, name, version));
            }
            if (plan is not null)
            {
                foreach (var name in plan.AurBuilds)
                {
                    var version = plan.AurVersions.TryGetValue(name, out var v) ? v : string.Empty;
                    planRows.Add(("aur", name, version));
                }
            }

            if (planRows.Count > 0)
            {
                UpgradeListFormatter.RenderPlan(planRows, "Packages to install", opt.GroupBy);
            }

            if (plan is not null && plan.Skipped.Count > 0)
            {
                foreach (var (p, why) in plan.Skipped)
                    Confirm.Status($"crumb: skip {p}: {why}");
            }
            if (opt.Upgrade)
            {
                var pending = await ListPendingUpgradesAsync(ct);
                if (pending.Count > 0)
                {
                    UpgradeListFormatter.RenderRepoUpgrades(pending, repoByName);
                }
            }

            if (!opt.NoConfirm && !opt.DryRun
                && (repoPkgs.Count > 0 || opt.Upgrade))
            {
                if (!Confirm.YesNo("Proceed?"))
                {
                    Console.Error.WriteLine("crumb: cancelled");
                    summary.Add(("Repo install", UpgradeListFormatter.ResultStatus.Skipped, "cancelled by user"));
                    UpgradeListFormatter.RenderSummary(summary);
                    return 1;
                }
            }

            var args = new List<string> { "pacman", "-S" };
            if (opt.Refresh) args[^1] += "y";
            if (opt.Upgrade) args[^1] += "u";
            args.Add("--noconfirm");
            if (opt.Needed) args.Add("--needed");
            var aurRepoDeps = plan?.RepoTargets ?? Array.Empty<string>();
            var userRepoOnly = repoPkgs.Where(r => !aurRepoDeps.Contains(r, StringComparer.Ordinal)).ToList();

            var repoCount = aurRepoDeps.Count + userRepoOnly.Count;
            if (aurRepoDeps.Count > 0)
            {
                var depArgs = new List<string>(args) { "--asdeps" };
                depArgs.AddRange(aurRepoDeps);
                var rcDeps = await RunEscalatedAsync(depArgs, opt.DryRun, ct);
                if (rcDeps != 0)
                {
                    summary.Add(("Repo install", UpgradeListFormatter.ResultStatus.Failed, $"pacman exit {rcDeps} (deps)"));
                    UpgradeListFormatter.RenderSummary(summary);
                    return rcDeps;
                }

                if (userRepoOnly.Count > 0)
                {
                    var ua = new List<string>(args);
                    if (opt.AsDeps) ua.Add("--asdeps");
                    ua.AddRange(userRepoOnly);
                    var rcU = await RunEscalatedAsync(ua, opt.DryRun, ct);
                    if (rcU != 0 && !opt.Upgrade)
                    {
                        summary.Add(("Repo install", UpgradeListFormatter.ResultStatus.Failed, $"pacman exit {rcU}"));
                        UpgradeListFormatter.RenderSummary(summary);
                        return rcU;
                    }
                }
                summary.Add(("Repo install", UpgradeListFormatter.ResultStatus.Success, $"{repoCount} package(s) installed"));
            }
            else if (repoPkgs.Count > 0 || opt.Upgrade)
            {
                if (opt.AsDeps) args.Add("--asdeps");
                args.AddRange(repoPkgs);
                var rc = await RunEscalatedAsync(args, opt.DryRun, ct);
                if (rc != 0 && !opt.Upgrade)
                {
                    summary.Add(("Repo install", UpgradeListFormatter.ResultStatus.Failed, $"pacman exit {rc}"));
                    UpgradeListFormatter.RenderSummary(summary);
                    return rc;
                }
                if (repoPkgs.Count > 0)
                    summary.Add(("Repo install", UpgradeListFormatter.ResultStatus.Success, $"{repoPkgs.Count} package(s) installed"));
            }
        }
        else if (aurPkgs.Count > 0 && plan is not null)
        {
            // Pure-AUR install: no repo packages, no upgrade. Render the
            // build plan and ask once before doing any clones.
            var aurRows = plan.AurBuilds
                .Select(n => ("aur", n, plan.AurVersions.TryGetValue(n, out var v) ? v : string.Empty))
                .ToList();
            if (aurRows.Count > 0)
                UpgradeListFormatter.RenderPlan(aurRows, "AUR packages to build", opt.GroupBy);

            if (plan.Skipped.Count > 0)
            {
                foreach (var (p, why) in plan.Skipped)
                    Confirm.Status($"crumb: skip {p}: {why}");
            }

            if (!opt.NoConfirm && !opt.DryRun && aurRows.Count > 0)
            {
                if (!Confirm.YesNo("Proceed?"))
                {
                    Console.Error.WriteLine("crumb: cancelled");
                    summary.Add(("AUR build", UpgradeListFormatter.ResultStatus.Skipped, "cancelled by user"));
                    UpgradeListFormatter.RenderSummary(summary);
                    return 1;
                }
            }
        }

        var aurRc = 0;
        // Paru-style: review is OFF by default; opt in with --review
        // or CRUMB_REVIEW=1. The historical --no-review flag is still
        // accepted as a no-op for muscle-memory compatibility.
        var reviewAur = (opt.Review
            || string.Equals(Environment.GetEnvironmentVariable("CRUMB_REVIEW"), "1", StringComparison.Ordinal))
            && !opt.NoReview;
        var develTracker = aurPkgs.Count > 0 ? DevelTracker.Load() : null;

        // Pre-clone every AUR target so a) the optional review sees
        // PKGBUILDs up front and b) a typo aborts before we spend
        // time on any actual build.
        var clonedTargets = new List<(string Pkg, string Dir)>();
        if (aurPkgs.Count > 0 && !opt.DryRun)
        {
            Confirm.Status($":: Fetching {aurPkgs.Count} PKGBUILD(s)...");
            foreach (var pkg in aurPkgs)
            {
                var dir = await AurBuilder.EnsureClonedAsync(pkg, ct);
                if (dir is null)
                {
                    Console.Error.WriteLine($"crumb: no PKGBUILD in clone of '{pkg}' — is the package name correct?");
                    summary.Add(("AUR build", UpgradeListFormatter.ResultStatus.Failed, $"clone failed: {pkg}"));
                    UpgradeListFormatter.RenderSummary(summary);
                    return 1;
                }
                clonedTargets.Add((pkg, dir));
            }

            if (reviewAur)
            {
                if (!await AurBuilder.BatchReviewAsync(clonedTargets, pagerOverride: null, opt.DiffReview, ct))
                {
                    Console.Error.WriteLine("crumb: cancelled");
                    summary.Add(("AUR build", UpgradeListFormatter.ResultStatus.Skipped, "cancelled at review"));
                    UpgradeListFormatter.RenderSummary(summary);
                    return 1;
                }
            }
        }

        var sudoLoop = (opt.SudoLoop || aurPkgs.Count > 1) && !opt.DryRun
            ? await SudoLoop.StartAsync(ct)
            : null;
        var aurOk = 0;
        var aurFail = 0;
        try
        {
            foreach (var pkg in aurPkgs)
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
        develTracker?.Save();
        if (aurPkgs.Count > 0 && !opt.DryRun)
        {
            if (aurFail > 0)
                summary.Add(("AUR build", UpgradeListFormatter.ResultStatus.Failed, $"{aurFail} failed, {aurOk} succeeded"));
            else if (aurOk > 0)
                summary.Add(("AUR build", UpgradeListFormatter.ResultStatus.Success, $"{aurOk} package(s) built"));
        }
        UpgradeListFormatter.RenderSummary(summary);
        return aurRc;
    }
}
