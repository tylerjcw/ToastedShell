using System.Diagnostics;

namespace Tosh.Tome;

internal enum GitFileStatus { Changed = 0, Untracked = 1, Deleted = 2 }

/// <summary>
/// Runs <c>git status --porcelain</c> for the workspace folders and
/// caches the result for a few seconds. Called from the render loop;
/// the cache ensures git is only spawned a few times per minute, not
/// every frame.
/// </summary>
internal sealed class ExplorerGitStatus : IDisposable
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private readonly string[] _roots;
    private Dictionary<string, GitFileStatus>? _status;
    private DateTime _lastRefresh = DateTime.MinValue;
    private bool _disposed;

    public ExplorerGitStatus(IEnumerable<string> roots)
    {
        _roots = roots.ToArray();
    }

    public void Invalidate() => _lastRefresh = DateTime.MinValue;

    public IReadOnlyDictionary<string, GitFileStatus>? GetStatus()
    {
        if (_disposed) return null;
        var now = DateTime.UtcNow;
        if (_status is not null && now - _lastRefresh < Ttl) return _status;
        _lastRefresh = now;
        _status = BuildStatus();
        return _status;
    }

    private Dictionary<string, GitFileStatus> BuildStatus()
    {
        var result = new Dictionary<string, GitFileStatus>(StringComparer.Ordinal);
        foreach (var root in _roots)
        {
            var gitRoot = GitInfo.FindRoot(root);
            if (gitRoot is null) continue;

            foreach (var line in RunGitStatus(gitRoot))
            {
                if (line.Length < 3) continue;
                var xy = line[..2];
                var rawPath = line[3..].Trim();
                // Porcelain renames: "old -> new"
                var arrow = rawPath.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrow >= 0) rawPath = rawPath[(arrow + 4)..];
                // Paths may be quoted by git when they contain special chars.
                if (rawPath.Length >= 2 && rawPath[0] == '"' && rawPath[^1] == '"')
                    rawPath = rawPath[1..^1];
                var full = Path.GetFullPath(Path.Combine(gitRoot, rawPath));
                var status = ParseStatus(xy);
                if (status.HasValue) result[full] = status.Value;
            }
        }
        return result;
    }

    private static GitFileStatus? ParseStatus(string xy)
    {
        if (xy[0] == 'D' || xy[1] == 'D') return GitFileStatus.Deleted;
        if (xy[0] == '?' && xy[1] == '?') return GitFileStatus.Untracked;
        if (xy[0] == '!' && xy[1] == '!') return null; // ignored
        return GitFileStatus.Changed;
    }

    private static string[] RunGitStatus(string workDir)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--no-pager");
            psi.ArgumentList.Add("status");
            psi.ArgumentList.Add("--porcelain");
            psi.ArgumentList.Add("--untracked-files=all");

            using var proc = Process.Start(psi);
            if (proc is null) return Array.Empty<string>();
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            if (!proc.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                try { proc.Kill(); } catch { }
                return Array.Empty<string>();
            }
            return stdoutTask.GetAwaiter().GetResult()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public void Dispose() => _disposed = true;
}
