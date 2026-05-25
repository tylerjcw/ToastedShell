namespace Tosh.Tome;

/// <summary>
/// Tiny git probe — walks up from a file/directory looking for a
/// <c>.git</c> entry, reads <c>HEAD</c>, and returns the current branch
/// (or short SHA when detached). Results are cached for a few seconds
/// per starting directory so the status bar doesn't hit the disk on
/// every keystroke.
/// </summary>
/// <remarks>
/// Deliberately does not shell out to <c>git</c>: it must be fast and
/// never spawn a subprocess from the render loop. "Dirty" detection
/// would require scanning the work tree, so we punt — branch only.
/// </remarks>
internal static class GitInfo
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);
    private static readonly Dictionary<string, (string? Branch, DateTime When)> _cache = new(StringComparer.Ordinal);
    private static readonly object _lock = new();

    public static string? GetBranch(string? filePath)
    {
        var start = ResolveStart(filePath);
        if (start is null) return null;

        lock (_lock)
        {
            if (_cache.TryGetValue(start, out var entry) && DateTime.UtcNow - entry.When < Ttl)
                return entry.Branch;
        }

        var branch = ProbeBranch(start);

        lock (_lock)
        {
            _cache[start] = (branch, DateTime.UtcNow);
        }
        return branch;
    }

    /// <summary>
    /// Returns the root directory of the git repo containing
    /// <paramref name="startPath"/> (file or directory), or null if not
    /// inside any repo.
    /// </summary>
    public static string? FindRoot(string? startPath)
    {
        try
        {
            string start;
            if (string.IsNullOrEmpty(startPath))
            {
                start = Environment.CurrentDirectory;
            }
            else
            {
                var full = Path.GetFullPath(startPath);
                start = Directory.Exists(full) ? full
                      : Path.GetDirectoryName(full) ?? Environment.CurrentDirectory;
            }
            return FindRootFrom(start);
        }
        catch { return null; }
    }

    private static string? FindRootFrom(string start)
    {
        try
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                var gitPath = Path.Combine(dir.FullName, ".git");
                if (Directory.Exists(gitPath) || File.Exists(gitPath)) return dir.FullName;
                dir = dir.Parent;
            }
        }
        catch { }
        return null;
    }

    private static string? ResolveStart(string? filePath)
    {
        try
        {
            if (!string.IsNullOrEmpty(filePath))
            {
                var full = Path.GetFullPath(filePath);
                // If it's already a directory start there; otherwise use its parent.
                var dir = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir)) return dir;
            }
            return Environment.CurrentDirectory;
        }
        catch
        {
            return null;
        }
    }

    private static string? ProbeBranch(string start)
    {
        var root = FindRootFrom(start);
        if (root is null) return null;
        return ReadHead(Path.Combine(root, ".git"));
    }

    private static string? ReadHead(string gitPath)
    {
        try
        {
            // Submodules / worktrees use a `.git` *file* pointing at the
            // real gitdir. Resolve one level of indirection.
            if (File.Exists(gitPath))
            {
                var contents = File.ReadAllText(gitPath).Trim();
                const string prefix = "gitdir:";
                if (contents.StartsWith(prefix, StringComparison.Ordinal))
                {
                    var target = contents.Substring(prefix.Length).Trim();
                    if (!Path.IsPathRooted(target))
                        target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(gitPath) ?? ".", target));
                    gitPath = target;
                }
            }
            var headFile = Path.Combine(gitPath, "HEAD");
            if (!File.Exists(headFile)) return null;
            var head = File.ReadAllText(headFile).Trim();
            const string refPrefix = "ref: refs/heads/";
            if (head.StartsWith(refPrefix, StringComparison.Ordinal))
                return head.Substring(refPrefix.Length);
            // Detached HEAD — return short SHA.
            return head.Length >= 7 ? head.Substring(0, 7) : head;
        }
        catch
        {
            return null;
        }
    }
}
