using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tosh.Crumb.Aur;

/// <summary>
/// Tracks upstream commits of VCS-style AUR packages so we can detect
/// when a rebuild is actually warranted, instead of either rebuilding
/// every <c>-git</c> package blindly or skipping them all. Models the
/// same idea as paru's <c>devel.toml</c>: per-package <c>{url, branch,
/// commit}</c> records, refreshed via <c>git ls-remote</c>.
///
/// The cache lives at <c>$XDG_CACHE_HOME/crumb/devel.json</c>.
/// Populated organically: whenever crumb successfully builds an AUR
/// package whose <c>.SRCINFO</c> declares any git source, the
/// upstream HEAD of every such source is recorded. <c>crumb --gendb</c>
/// seeds the cache for already-installed packages.
/// </summary>
public sealed class DevelTracker
{
    private const int SchemaVersion = 1;

    private static string CacheDir
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            var root = !string.IsNullOrEmpty(xdg)
                ? xdg!
                : System.IO.Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "~", ".cache");
            return System.IO.Path.Combine(root, "crumb");
        }
    }

    private static string CacheFile => System.IO.Path.Combine(CacheDir, "devel.json");

    private DevelState _state;
    private DevelTracker(DevelState s) { _state = s; }

    public static DevelTracker Load()
    {
        try
        {
            if (File.Exists(CacheFile))
            {
                var json = File.ReadAllText(CacheFile);
                var s = JsonSerializer.Deserialize<DevelState>(json, JsonOpts);
                if (s is { Packages: not null }) return new DevelTracker(s);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"crumb: warning: failed to read devel cache: {ex.Message}");
        }
        return new DevelTracker(new DevelState { Version = SchemaVersion, Packages = new() });
    }

    public bool Tracks(string pkg) => _state.Packages.ContainsKey(pkg);

    public IReadOnlyCollection<string> TrackedPackages => _state.Packages.Keys;

    /// <summary>
    /// Drops <paramref name="pkg"/> from the cache. Used after
    /// <c>pacman -R</c> succeeds so we don't keep stale commit
    /// records around for packages the user no longer has.
    /// </summary>
    public bool Forget(string pkg) => _state.Packages.Remove(pkg);

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var tmp = CacheFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_state, JsonOpts));
            File.Move(tmp, CacheFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"crumb: warning: failed to save devel cache: {ex.Message}");
        }
    }

    /// <summary>
    /// Parse <c>srcInfoPath</c>, identify git sources, run
    /// <c>git ls-remote</c> against each, and store the resulting
    /// commits under <paramref name="pkg"/>. Existing entries for the
    /// same package are replaced. Returns true if anything was
    /// recorded (i.e. the package has at least one git source).
    /// </summary>
    /// <param name="baselineCommit">
    /// Optional short or full commit hash to record instead of the
    /// current remote HEAD. Useful for <c>gendb</c>: by extracting
    /// the hash embedded in the installed <c>pkgver</c> (the
    /// <c>.g&lt;sha&gt;</c> suffix of git VCS packages) the cache can
    /// detect upstream movement without first needing a rebuild.
    /// </param>
    public async Task<bool> RecordAsync(string pkg, string srcInfoPath, CancellationToken ct, string? baselineCommit = null)
    {
        if (!File.Exists(srcInfoPath)) return false;
        var sources = ParseGitSources(await File.ReadAllLinesAsync(srcInfoPath, ct));
        if (sources.Count == 0) return false;

        var recorded = new List<SourceEntry>(sources.Count);
        foreach (var (url, branch) in sources)
        {
            string? sha = baselineCommit;
            if (sha is null)
            {
                sha = await LsRemoteAsync(url, branch, ct);
                if (sha is null) continue;
            }
            recorded.Add(new SourceEntry { Url = url, Branch = branch, Commit = sha });
        }
        if (recorded.Count == 0) return false;
        _state.Packages[pkg] = new PkgEntry { Sources = recorded };
        return true;
    }

    /// <summary>
    /// For each tracked package in <paramref name="candidates"/>,
    /// ls-remote its recorded sources and return packages whose
    /// upstream HEAD has moved. Network failures are treated as
    /// "no update" (better to skip than to spam rebuilds).
    /// </summary>
    public async Task<List<string>> CheckUpdatesAsync(IEnumerable<string> candidates, CancellationToken ct)
    {
        var updated = new List<string>();
        foreach (var pkg in candidates)
        {
            if (!_state.Packages.TryGetValue(pkg, out var entry)) continue;
            foreach (var s in entry.Sources)
            {
                var sha = await LsRemoteAsync(s.Url, s.Branch, ct);
                if (sha is null) continue;
                if (!CommitsMatch(s.Commit, sha))
                {
                    updated.Add(pkg);
                    break;
                }
            }
        }
        return updated;
    }

    /// <summary>
    /// Compares a stored commit (potentially a short prefix from a
    /// pkgver string like <c>r28.g073987f</c>) against a full SHA
    /// from <c>git ls-remote</c>. Match if either is a prefix of the
    /// other.
    /// </summary>
    private static bool CommitsMatch(string stored, string remote)
    {
        if (stored.Length == 0 || remote.Length == 0) return false;
        var shorter = stored.Length <= remote.Length ? stored : remote;
        var longer = stored.Length <= remote.Length ? remote : stored;
        return longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parse <c>.SRCINFO</c> lines and pull out git-style sources.
    /// Recognised forms (after stripping the optional <c>name::</c>
    /// prefix): <c>git+https://…</c>, <c>git://…</c>,
    /// <c>git+ssh://…</c>. Sources pinned to a specific commit or tag
    /// (<c>#commit=…</c>/<c>#tag=…</c>) are skipped — they cannot
    /// move. The optional <c>#branch=…</c> fragment is captured.
    /// </summary>
    private static List<(string Url, string? Branch)> ParseGitSources(IEnumerable<string> srcInfoLines)
    {
        var result = new List<(string, string?)>();
        foreach (var raw in srcInfoLines)
        {
            var line = raw.TrimStart();
            if (!line.StartsWith("source")) continue;
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var value = line[(eq + 1)..].Trim();
            if (value.Length == 0) continue;

            var dcolon = value.IndexOf("::", StringComparison.Ordinal);
            var url = dcolon >= 0 ? value[(dcolon + 2)..] : value;

            // AUR source syntax: <vcs>[+<scheme>]://...  where <vcs> is
            // one of git/hg/svn/bzr and <scheme> is the wire protocol
            // (https/ssh/git). We only care about git here — it is the
            // only one with a cheap ls-remote API. Use paru's split:
            // the part before "://" is "<vcs>[+<scheme>]"; the bit
            // after the last '+' is the wire scheme.
            var protoEnd = url.IndexOf("://", StringComparison.Ordinal);
            if (protoEnd < 0) continue;
            var protoSpec = url[..protoEnd];
            if (!protoSpec.StartsWith("git", StringComparison.Ordinal)) continue;
            var scheme = protoSpec.Contains('+')
                ? protoSpec.Split('+').Last()
                : protoSpec;

            string? branch = null;
            var hash = url.IndexOf('#');
            string remote;
            if (hash >= 0)
            {
                remote = url[..hash];
                var frag = url[(hash + 1)..];
                var q = frag.IndexOf('?');
                if (q >= 0) frag = frag[..q];
                var kv = frag.Split('=', 2);
                if (kv.Length == 2)
                {
                    switch (kv[0])
                    {
                        case "commit":
                        case "tag":
                            continue;            // pinned, never moves
                        case "branch":
                            branch = kv[1];
                            break;
                    }
                }
            }
            else
            {
                var q = url.IndexOf('?');
                remote = q >= 0 ? url[..q] : url;
            }

            // Normalise the protocol prefix: keep the wire scheme
            // (drop AUR's `git+` decoration).
            var schemeIdx = remote.IndexOf("://", StringComparison.Ordinal);
            var rest = remote[(schemeIdx + 3)..];
            remote = $"{scheme}://{rest}";

            result.Add((remote, branch));
        }
        return result;
    }

    private static async Task<string?> LsRemoteAsync(string url, string? branch, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.ArgumentList.Add("ls-remote");
        psi.ArgumentList.Add(url);
        psi.ArgumentList.Add(branch ?? "HEAD");
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return null;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            var stdout = await p.StandardOutput.ReadToEndAsync(cts.Token);
            await p.WaitForExitAsync(cts.Token);
            if (p.ExitCode != 0) return null;
            var firstLine = stdout.Split('\n', 2)[0];
            var sha = firstLine.Split('\t', 2)[0].Trim();
            return sha.Length == 0 ? null : sha;
        }
        catch
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class DevelState
    {
        [JsonPropertyName("version")] public int Version { get; set; } = SchemaVersion;
        [JsonPropertyName("packages")] public Dictionary<string, PkgEntry> Packages { get; set; } = new();
    }

    private sealed class PkgEntry
    {
        [JsonPropertyName("sources")] public List<SourceEntry> Sources { get; set; } = new();
    }

    private sealed class SourceEntry
    {
        [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
        [JsonPropertyName("branch")] public string? Branch { get; set; }
        [JsonPropertyName("commit")] public string Commit { get; set; } = string.Empty;
    }
}
