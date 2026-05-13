using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tosh.Crumb.Aur;

/// <summary>
/// Remembers the last PKGBUILD commit the user approved per package.
/// On the next build, if the clone has moved forward we only show
/// the diff instead of the whole PKGBUILD — same UX as paru's
/// <c>view ?</c> + diff-vs-last-reviewed flow.
///
/// Cache lives at <c>$XDG_CACHE_HOME/crumb/reviews.json</c>; missing
/// or unreadable cache is treated as "never reviewed" so the user
/// still sees the full PKGBUILD on first run.
/// </summary>
public sealed class ReviewCache
{
    private static string CacheDir
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            var root = !string.IsNullOrEmpty(xdg)
                ? xdg!
                : Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "~", ".cache");
            return Path.Combine(root, "crumb");
        }
    }

    private static string CacheFile => Path.Combine(CacheDir, "reviews.json");

    private ReviewState _state;
    private ReviewCache(ReviewState s) { _state = s; }

    public static ReviewCache Load()
    {
        try
        {
            if (File.Exists(CacheFile))
            {
                var json = File.ReadAllText(CacheFile);
                var s = JsonSerializer.Deserialize<ReviewState>(json, JsonOpts);
                if (s is not null) return new ReviewCache(s);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"crumb: warning: failed to read review cache: {ex.Message}");
        }
        return new ReviewCache(new ReviewState());
    }

    public string? LastReviewed(string pkg)
        => _state.Reviewed.TryGetValue(pkg, out var sha) ? sha : null;

    public void Record(string pkg, string sha)
        => _state.Reviewed[pkg] = sha;

    public void Forget(string pkg) => _state.Reviewed.Remove(pkg);

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
            Console.Error.WriteLine($"crumb: warning: failed to save review cache: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the current <c>HEAD</c> commit SHA of the git
    /// checkout at <paramref name="dir"/>, or null if it isn't a git
    /// repo (e.g. a manually-extracted snapshot).
    /// </summary>
    public static async Task<string?> HeadShaAsync(string dir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("rev-parse");
        psi.ArgumentList.Add("HEAD");
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return null;
            var line = await p.StandardOutput.ReadLineAsync(ct);
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0) return null;
            return string.IsNullOrWhiteSpace(line) ? null : line.Trim();
        }
        catch { return null; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class ReviewState
    {
        [JsonPropertyName("reviewed")] public Dictionary<string, string> Reviewed { get; set; } = new();
    }
}
