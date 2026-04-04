using System.Diagnostics;
using System.Globalization;

namespace Tosh.Core;

public static class PromptSegmentUtilities
{
    public static StyledText BuildDirectorySegment(string currentDirectory, int? maxDepth, ToshTextStyleConfig style)
    {
        ArgumentNullException.ThrowIfNull(currentDirectory);
        ArgumentNullException.ThrowIfNull(style);
        return ApplyStyle(style, ShortenPath(currentDirectory, maxDepth));
    }

    public static StyledText BuildDirectorySegment(string currentDirectory, int? maxDepth, string? foreground, string? background, bool bold)
    {
        ArgumentNullException.ThrowIfNull(currentDirectory);
        return new StyledText(ShortenPath(currentDirectory, maxDepth), foreground, background, bold);
    }

    public static StyledText? BuildGitSegment(string currentDirectory, ToshTextStyleConfig style)
    {
        ArgumentNullException.ThrowIfNull(currentDirectory);
        ArgumentNullException.ThrowIfNull(style);

        var info = GetGitInfo(currentDirectory);

        if (info is null)
        {
            return null;
        }

        var statusColor = info.Value.Dirty
            ? "yellow"
            : info.Value.Staged
                ? "bright-green"
                : style.Foreground;

        return new StyledText(
            BuildGitText(info.Value),
            statusColor,
            style.Background,
            style.Bold,
            style.Italic,
            style.Underline,
            style.Dim);
    }

    public static StyledText? BuildGitSegment(string currentDirectory, string? foreground, string? background, bool bold)
    {
        ArgumentNullException.ThrowIfNull(currentDirectory);

        var info = GetGitInfo(currentDirectory);

        if (info is null)
        {
            return null;
        }

        var statusColor = info.Value.Dirty ? "yellow" : info.Value.Staged ? "bright-green" : foreground;
        return new StyledText(BuildGitText(info.Value), statusColor, background, bold);
    }

    public static StyledText BuildTimeSegment(DateTimeOffset timestamp, string? format, ToshTextStyleConfig style)
    {
        ArgumentNullException.ThrowIfNull(style);
        return ApplyStyle(style, FormatTimestamp(timestamp, format));
    }

    public static StyledText BuildTimeSegment(DateTimeOffset timestamp, string? format, string? foreground, string? background, bool bold, bool italic = false, bool underline = false, bool dim = false)
    {
        return new StyledText(FormatTimestamp(timestamp, format), foreground, background, bold, italic, underline, dim);
    }

    public static StyledText? BuildExitCodeSegment(int exitCode, ToshTextStyleConfig style)
    {
        ArgumentNullException.ThrowIfNull(style);

        if (exitCode == 0)
        {
            return null;
        }

        return ApplyStyle(style, $"✘ {exitCode}");
    }

    public static StyledText? BuildExitCodeSegment(int exitCode, string? foreground, string? background, bool bold, bool italic = false, bool underline = false, bool dim = false)
    {
        if (exitCode == 0)
        {
            return null;
        }

        return new StyledText($"✘ {exitCode}", foreground, background, bold, italic, underline, dim);
    }

    public static StyledText BuildHistoryIdSegment(long historyId, ToshTextStyleConfig style)
    {
        ArgumentNullException.ThrowIfNull(style);
        return ApplyStyle(style, $"!{Math.Max(1, historyId)}");
    }

    public static StyledText BuildHistoryIdSegment(long historyId, string? foreground, string? background, bool bold, bool italic = false, bool underline = false, bool dim = false)
    {
        return new StyledText($"!{Math.Max(1, historyId)}", foreground, background, bold, italic, underline, dim);
    }

    public static StyledText BuildUserHostSegment(string userName, string hostName, ToshTextStyleConfig style)
    {
        ArgumentNullException.ThrowIfNull(style);
        return ApplyStyle(style, $"{userName}@{hostName}");
    }

    public static StyledText BuildJobsSegment(int jobCount, ToshTextStyleConfig style)
    {
        ArgumentNullException.ThrowIfNull(style);
        return ApplyStyle(style, $"jobs:{jobCount}");
    }

    public static StyledText? BuildDurationSegment(TimeSpan? duration, TimeSpan threshold, ToshTextStyleConfig style)
    {
        ArgumentNullException.ThrowIfNull(style);

        if (duration is null || duration.Value <= TimeSpan.Zero || duration.Value < threshold)
        {
            return null;
        }

        return ApplyStyle(style, FormatDuration(duration.Value));
    }

    public static string ShortenPath(string path, int? maxDepth = null)
    {
        ArgumentNullException.ThrowIfNull(path);

        var aliases = PathUtilities.DirectoryAliases;
        var bestAlias = aliases?.TryReverseLookup(path);

        if (bestAlias is not null && aliases!.TryResolve(bestAlias, out var aliasPath))
        {
            path = path.Length == aliasPath.Length
                ? $"~{bestAlias}"
                : $"~{bestAlias}{path[aliasPath.Length..]}";
        }
        else
        {
            var home = PathUtilities.UserHomeDirectory;

            if (path.StartsWith(home, PathUtilities.GetPathComparison()))
            {
                path = $"~{path[home.Length..]}";
            }
        }

        if (maxDepth is int limit && limit > 0)
        {
            var separator = Path.DirectorySeparatorChar;
            var parts = path.Split(separator, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length > limit)
            {
                var prefix = path.StartsWith('~') ? "~" : path.StartsWith(separator) ? separator.ToString() : "";
                var truncated = string.Join(separator, parts[^limit..]);
                path = $"{prefix}{(prefix.Length > 0 ? separator.ToString() : string.Empty)}…{separator}{truncated}";
            }
        }

        return path;
    }

    private static StyledText ApplyStyle(ToshTextStyleConfig style, string text)
    {
        return new StyledText(text, style.Foreground, style.Background, style.Bold, style.Italic, style.Underline, style.Dim);
    }

    private static string FormatTimestamp(DateTimeOffset timestamp, string? format)
    {
        var effectiveFormat = string.IsNullOrWhiteSpace(format) ? "HH:mm" : format;

        try
        {
            return timestamp.ToString(effectiveFormat, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return timestamp.ToString("HH:mm", CultureInfo.InvariantCulture);
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{duration.TotalHours:0.#}h";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{duration.TotalMinutes:0.#}m";
        }

        if (duration.TotalSeconds >= 1)
        {
            return $"{duration.TotalSeconds:0.#}s";
        }

        return $"{Math.Max(1, (int)Math.Round(duration.TotalMilliseconds, MidpointRounding.AwayFromZero))}ms";
    }

    private static string BuildGitText(PromptGitInfo info)
    {
        var status = new System.Text.StringBuilder();
        status.Append(info.Branch);

        if (info.Dirty || info.Staged)
        {
            if (info.Staged)
            {
                status.Append('+');
            }

            if (info.Dirty)
            {
                status.Append('*');
            }
        }

        if (info.Ahead > 0)
        {
            status.Append($" ↑{info.Ahead}");
        }

        if (info.Behind > 0)
        {
            status.Append($" ↓{info.Behind}");
        }

        status.Append(' ');
        status.Append('\ue0a0');
        return status.ToString();
    }

    private static PromptGitInfo? GetGitInfo(string directory)
    {
        try
        {
            var branch = RunGit(directory, "rev-parse --abbrev-ref HEAD");

            if (branch is null)
            {
                return null;
            }

            var statusOutput = RunGit(directory, "status --porcelain=v1") ?? string.Empty;
            var dirty = false;
            var staged = false;

            foreach (var line in statusOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length < 2)
                {
                    continue;
                }

                if (line[1] != ' ' && line[1] != '?')
                {
                    dirty = true;
                }

                if (line[0] != ' ' && line[0] != '?')
                {
                    staged = true;
                }
            }

            var ahead = 0;
            var behind = 0;
            var aheadBehind = RunGit(directory, "rev-list --left-right --count HEAD...@{upstream}");

            if (aheadBehind is not null)
            {
                var parts = aheadBehind.Split('\t', StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length == 2)
                {
                    int.TryParse(parts[0], out ahead);
                    int.TryParse(parts[1], out behind);
                }
            }

            return new PromptGitInfo(branch.Trim(), dirty, staged, ahead, behind);
        }
        catch
        {
            return null;
        }
    }

    private static string? RunGit(string workingDirectory, string arguments)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            process.Start();

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);

            return process.ExitCode == 0 ? output.TrimEnd() : null;
        }
        catch
        {
            return null;
        }
    }

    private readonly record struct PromptGitInfo(string Branch, bool Dirty, bool Staged, int Ahead, int Behind);
}
