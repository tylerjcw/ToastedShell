using System.Diagnostics;

namespace Tosh.Tome;

/// <summary>
/// Kind of diff annotation for a single line, used by the gutter renderer
/// to draw a green/yellow/red bar. <see cref="Deleted"/> is reserved for
/// future use — the current single-column renderer doesn't show it.
/// </summary>
internal enum DiffKind { Added, Modified, Deleted }

/// <summary>
/// Per-tab git diff state. Spawns <c>git diff --no-color -U0 HEAD -- &lt;path&gt;</c>
/// on demand (rate-limited by <see cref="RefreshInterval"/>) and parses
/// the unified-diff hunk headers to mark added/modified buffer lines.
/// Deleted lines are recorded but not currently rendered.
/// </summary>
/// <remarks>
/// Falls back silently to an empty diff when git is missing, the file is
/// outside any repo, or git exits non-zero. The result is approximate —
/// it reflects HEAD vs the working tree on disk, not the unsaved buffer
/// contents. That mismatch is acceptable: the bars still show roughly
/// where the file diverges from HEAD, and refresh on save.
/// </remarks>
internal sealed class GitDiffTracker
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(3);

    private readonly string _filePath;
    private Dictionary<int, DiffKind> _diff = new();
    private DateTime _lastRefresh = DateTime.MinValue;
    private bool _gitMissing;

    public GitDiffTracker(string filePath) { _filePath = filePath; }

    /// <summary>
    /// Returns the cached per-line diff map, refreshing it in-process if
    /// the rate-limit window has elapsed. Callers must not mutate the
    /// returned dictionary.
    /// </summary>
    public IReadOnlyDictionary<int, DiffKind> GetDiff()
    {
        if (_gitMissing) return _diff;
        if (DateTime.UtcNow - _lastRefresh < RefreshInterval) return _diff;
        Refresh();
        return _diff;
    }

    /// <summary>Force a refresh on next call (e.g. after save).</summary>
    public void Invalidate() => _lastRefresh = DateTime.MinValue;

    private void Refresh()
    {
        _lastRefresh = DateTime.UtcNow;
        if (string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath))
        {
            _diff = new Dictionary<int, DiffKind>();
            return;
        }

        string? output;
        try
        {
            output = RunGit(Path.GetDirectoryName(_filePath) ?? ".", _filePath);
        }
        catch
        {
            _gitMissing = true;
            _diff = new Dictionary<int, DiffKind>();
            return;
        }

        if (output is null)
        {
            _diff = new Dictionary<int, DiffKind>();
            return;
        }

        _diff = ParseHunks(output);
    }

    private static string? RunGit(string workingDir, string path)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--no-pager");
        psi.ArgumentList.Add("diff");
        psi.ArgumentList.Add("--no-color");
        psi.ArgumentList.Add("-U0");
        psi.ArgumentList.Add("HEAD");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(path);

        using var proc = Process.Start(psi);
        if (proc is null) return null;
        var stdout = proc.StandardOutput.ReadToEnd();
        if (!proc.WaitForExit(750)) { try { proc.Kill(); } catch { } return null; }
        return proc.ExitCode == 0 ? stdout : null;
    }

    private static Dictionary<int, DiffKind> ParseHunks(string diffOutput)
    {
        var result = new Dictionary<int, DiffKind>();
        foreach (var raw in diffOutput.Split('\n'))
        {
            if (!raw.StartsWith("@@ ")) continue;
            // Format: @@ -oldStart[,oldLen] +newStart[,newLen] @@
            var endIdx = raw.IndexOf(" @@", 3, StringComparison.Ordinal);
            if (endIdx < 0) continue;
            var header = raw.Substring(3, endIdx - 3);
            var parts = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            if (!TryParseRange(parts[0], out var oldLen)) continue;
            if (!TryParseRange(parts[1], out var newLen)) continue;
            var newStart = ParseStart(parts[1]);
            if (newStart < 0) continue;

            if (newLen == 0)
            {
                // Pure deletion at oldStart in base; the buffer line
                // immediately AFTER the deletion (newStart) gets the
                // marker as a placeholder for "something was removed".
                if (newStart >= 0 && !result.ContainsKey(newStart))
                    result[newStart] = DiffKind.Deleted;
                continue;
            }

            var kind = oldLen == 0 ? DiffKind.Added : DiffKind.Modified;
            for (var i = 0; i < newLen; i++)
            {
                var line = newStart + i;
                // Modified beats Added if both apply; first write wins.
                if (!result.TryGetValue(line, out var existing) || kind < existing)
                    result[line] = kind;
            }
        }
        return result;
    }

    private static bool TryParseRange(string token, out int length)
    {
        length = 0;
        if (token.Length < 2) return false;
        var body = token.AsSpan(1); // drop leading '-' or '+'
        var commaIdx = body.IndexOf(',');
        if (commaIdx < 0) { length = 1; return true; }
        return int.TryParse(body[(commaIdx + 1)..], out length);
    }

    private static int ParseStart(string token)
    {
        if (token.Length < 2) return -1;
        var body = token.AsSpan(1);
        var commaIdx = body.IndexOf(',');
        var startSpan = commaIdx < 0 ? body : body[..commaIdx];
        if (!int.TryParse(startSpan, out var n)) return -1;
        // git uses 1-based line numbers; gutter wants 0-based.
        return Math.Max(0, n - 1);
    }
}
