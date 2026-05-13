using Tosh.Crumb.Models;
using Tosh.Crumb.Pacman;

namespace Tosh.Crumb.Aur;

/// <summary>
/// Builds a transitive install plan for a mix of repo and AUR
/// targets. Same shape as paru's <c>aur_depends::Actions</c> — repo
/// deps split out, AUR packages topologically ordered (deepest dep
/// first), conflicts detected against the local DB, and any
/// completely-unknown names surfaced as <see cref="Missing"/>.
///
/// Limitations vs paru:
///   * Version constraints in dep strings (<c>foo&gt;=1.2</c>) are
///     stripped — we only resolve by name.
///   * <c>provides</c> resolution is best-effort: a dep matches if
///     it equals a package name OR appears in any package's
///     <c>Provides</c> list (also stripped of version constraints).
///   * Optional deps are not pulled in automatically.
///   * Groups are not expanded; the user has to spell members
///     explicitly. (Paru defers to pacman for <c>-S &lt;group&gt;</c>
///     and we do the same when the name is a repo group.)
///
/// These constraints are documented; the resolver is deliberately
/// kept simple so we don't ship a half-broken alpm clone.
/// </summary>
public sealed class DependencyResolver
{
    private readonly PacmanDb _db;
    private readonly AurClient _aur;
    private readonly HashSet<string> _repoNames;
    private readonly Dictionary<string, Package> _repoByName;
    private readonly Dictionary<string, Package> _repoByProvides;
    private readonly bool _needed;

    public DependencyResolver(PacmanDb db, AurClient aur, bool needed)
    {
        _db = db;
        _aur = aur;
        _needed = needed;
        _repoNames = new(StringComparer.Ordinal);
        _repoByName = new(StringComparer.Ordinal);
        _repoByProvides = new(StringComparer.Ordinal);
        foreach (var p in db.Sync)
        {
            _repoNames.Add(p.Name);
            _repoByName.TryAdd(p.Name, p);
            foreach (var pr in p.Provides)
                _repoByProvides.TryAdd(StripConstraint(pr), p);
        }
    }

    public async Task<ResolvedPlan> ResolveAsync(IEnumerable<string> targets, CancellationToken ct)
    {
        var repoSet = new HashSet<string>(StringComparer.Ordinal);
        var aurInfo = new Dictionary<string, Package>(StringComparer.Ordinal);
        var aurOrder = new List<string>();
        var missing = new List<string>();
        var skipped = new List<(string, string)>();
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        var explicitTargets = targets.ToList();

        // First pass: bulk-fetch AUR info for every name we don't see
        // in the repo set. Saves a network round-trip per package
        // when the user gives us a wide install list.
        var notInRepo = explicitTargets.Where(t => !_repoNames.Contains(t) && !_repoByProvides.ContainsKey(t)).ToList();
        if (notInRepo.Count > 0)
        {
            try
            {
                var infos = await _aur.InfoAsync(notInRepo, ct);
                foreach (var p in infos) aurInfo[p.Name] = p;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"crumb: warning: AUR info pre-fetch failed: {ex.Message}");
            }
        }

        foreach (var t in explicitTargets)
            await VisitAsync(t, isExplicit: true);

        return new ResolvedPlan(
            RepoTargets: repoSet.OrderBy(n => n, StringComparer.Ordinal).ToList(),
            AurBuilds: aurOrder,
            Skipped: skipped,
            Missing: missing,
            Conflicts: ComputeConflicts(aurOrder.Select(n => aurInfo[n])),
            AurVersions: aurOrder.ToDictionary(n => n, n => aurInfo[n].Version, StringComparer.Ordinal));

        async Task VisitAsync(string name, bool isExplicit)
        {
            ct.ThrowIfCancellationRequested();
            var key = StripConstraint(name);
            if (visited.Contains(key)) return;
            if (visiting.Contains(key))
            {
                // Cycle: don't loop forever. Trust that whoever
                // closes the cycle will install the package.
                return;
            }

            // Already installed?  Skip unless the caller asked for
            // a forced rebuild (we don't expose that yet).
            if (_db.Local.TryGetValue(key, out var inst) && _needed && !isExplicit)
            {
                visited.Add(key);
                skipped.Add((key, $"installed ({inst.Version})"));
                return;
            }
            if (_db.Local.ContainsKey(key) && _needed && isExplicit)
            {
                visited.Add(key);
                skipped.Add((key, "already installed"));
                return;
            }

            // Repo?
            if (_repoByName.TryGetValue(key, out var repo))
            {
                repoSet.Add(repo.Name);
                visited.Add(key);
                return;
            }
            if (_repoByProvides.TryGetValue(key, out var providerRepo))
            {
                repoSet.Add(providerRepo.Name);
                visited.Add(key);
                return;
            }

            // AUR?
            if (!aurInfo.TryGetValue(key, out var aur))
            {
                try
                {
                    var fetched = await _aur.InfoAsync(new[] { key }, ct);
                    if (fetched.Count > 0)
                    {
                        aur = fetched[0];
                        aurInfo[aur.Name] = aur;
                    }
                }
                catch { /* swallow; surface as missing */ }
            }

            if (aur is null)
            {
                visited.Add(key);
                missing.Add(key);
                return;
            }

            visiting.Add(key);
            foreach (var d in aur.Depends.Concat(aur.MakeDepends).Concat(aur.CheckDepends))
                await VisitAsync(d, isExplicit: false);
            visiting.Remove(key);
            visited.Add(key);
            aurOrder.Add(aur.Name);
        }
    }

    private List<(string Pkg, string With)> ComputeConflicts(IEnumerable<Package> aurBuilds)
    {
        var result = new List<(string, string)>();
        foreach (var pkg in aurBuilds)
        {
            foreach (var c in pkg.Conflicts)
            {
                var name = StripConstraint(c);
                if (string.Equals(name, pkg.Name, StringComparison.Ordinal)) continue;
                if (_db.Local.ContainsKey(name))
                    result.Add((pkg.Name, name));
            }
        }
        return result;
    }

    private static string StripConstraint(string dep)
    {
        // "foo>=1.2: optional description" → "foo"
        var colon = dep.IndexOf(':');
        if (colon > 0) dep = dep[..colon];
        var op = dep.AsSpan().IndexOfAny('>', '<', '=');
        if (op >= 0) dep = dep[..op];
        return dep.Trim();
    }
}

/// <summary>
/// Output of <see cref="DependencyResolver.ResolveAsync"/>. Repos
/// are unordered (pacman handles topology); AUR builds are in
/// topological order — install <c>AurBuilds[0]</c> first.
/// </summary>
public sealed record ResolvedPlan(
    IReadOnlyList<string> RepoTargets,
    IReadOnlyList<string> AurBuilds,
    IReadOnlyList<(string Pkg, string Why)> Skipped,
    IReadOnlyList<string> Missing,
    IReadOnlyList<(string Pkg, string With)> Conflicts,
    IReadOnlyDictionary<string, string> AurVersions);
