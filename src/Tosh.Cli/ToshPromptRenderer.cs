using Tosh.Runtime;

namespace Tosh.Cli;

internal static class ToshPromptRenderer
{
    internal static readonly IReadOnlyList<string> SupportedModuleNames =
    [
        "Time",
        "Directory",
        "Git",
        "UserHost",
        "HistoryId",
        "Jobs",
        "Duration",
        "ExitCode",
        "Name",
        "Indicator",
    ];

    public static string BuildDefaultPrompt(ToshRuntime runtime, int? width = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var result = BuildPromptLayout(runtime, CreateLiveContext(runtime), width ?? GetConsoleWidth());
        return string.Join("\n", result.Lines);
    }

    public static IReadOnlyList<string> BuildPreviewLines(ToshRuntime runtime, int exitCode, int width)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return BuildPromptLayout(runtime, CreatePreviewContext(runtime, exitCode), Math.Max(40, width)).Lines;
    }

    public static IReadOnlyList<string> GetLayoutModules(string layout)
    {
        return ParseLayout(layout)
            .Select(NormalizeModuleName)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()!;
    }

    public static IReadOnlyList<string> GetUnknownLayoutModules(string layout)
    {
        return ParseLayout(layout)
            .Where(static name => !IsSupportedModule(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static PromptRenderResult BuildPromptLayout(ToshRuntime runtime, PromptRenderContext context, int width)
    {
        var promptConfig = runtime.Config.Prompt;
        var headerLeft = BuildSegmentList(runtime, context, promptConfig.HeaderLeftLayout);
        var headerRight = BuildSegmentList(runtime, context, promptConfig.HeaderRightLayout);
        var promptLeft = BuildSegmentList(runtime, context, promptConfig.PromptLeftLayout);

        var lines = new List<string>();

        if (headerLeft.Count > 0 || headerRight.Count > 0)
        {
            lines.Add(ComposeHeaderLine(headerLeft, headerRight, width));
        }

        lines.Add(RenderSegmentRun(promptLeft));
        return new PromptRenderResult(lines);
    }

    private static List<object?> BuildSegmentList(ToshRuntime runtime, PromptRenderContext context, string layout)
    {
        var promptConfig = runtime.Config.Prompt;
        var theme = runtime.Config.Theme.Prompt;
        var segments = new List<object?>();

        foreach (var module in ParseLayout(layout))
        {
            switch (NormalizeModuleName(module))
            {
                case "Time" when promptConfig.TimeEnabled:
                    segments.Add(PromptSegmentUtilities.BuildTimeSegment(context.Now, promptConfig.TimeFormat, theme.Time));
                    break;
                case "Directory":
                    segments.Add(PromptSegmentUtilities.BuildDirectorySegment(context.CurrentDirectory, promptConfig.DirectoryDepth, theme.Directory));
                    break;
                case "Git" when promptConfig.GitEnabled:
                    {
                        var gitSegment = PromptSegmentUtilities.BuildGitSegment(context.CurrentDirectory, theme.Git);

                        if (gitSegment is not null)
                        {
                            segments.Add(gitSegment);
                        }

                        break;
                    }
                case "UserHost" when promptConfig.UserHostEnabled:
                    segments.Add(PromptSegmentUtilities.BuildUserHostSegment(context.UserName, context.HostName, theme.UserHost));
                    break;
                case "HistoryId" when promptConfig.HistoryIdEnabled:
                    segments.Add(PromptSegmentUtilities.BuildHistoryIdSegment(context.NextHistoryId, theme.HistoryId));
                    break;
                case "Jobs" when promptConfig.JobsEnabled && context.JobCount > 0:
                    segments.Add(PromptSegmentUtilities.BuildJobsSegment(context.JobCount, theme.Jobs));
                    break;
                case "Duration" when promptConfig.DurationEnabled:
                    {
                        var durationSegment = PromptSegmentUtilities.BuildDurationSegment(
                            context.LastDuration,
                            TimeSpan.FromMilliseconds(promptConfig.DurationThresholdMilliseconds),
                            theme.Duration);

                        if (durationSegment is not null)
                        {
                            segments.Add(durationSegment);
                        }

                        break;
                    }
                case "ExitCode" when promptConfig.ExitCodeEnabled:
                    {
                        var exitCodeSegment = PromptSegmentUtilities.BuildExitCodeSegment(context.ExitCode, theme.ExitCode);

                        if (exitCodeSegment is not null)
                        {
                            segments.Add(exitCodeSegment);
                        }

                        break;
                    }
                case "Name":
                    segments.Add(theme.Name.Apply(promptConfig.NameText));
                    break;
                case "Indicator":
                    {
                        var indicator = TerminalGlyphs.IsActive
                            ? TerminalGlyphs.Indicator
                            : promptConfig.IndicatorText;
                        segments.Add(theme.Indicator.Apply(indicator));
                        break;
                    }
            }
        }

        return segments;
    }

    private static string ComposeHeaderLine(IReadOnlyList<object?> leftSegments, IReadOnlyList<object?> rightSegments, int width)
    {
        var left = RenderSegmentRun(leftSegments);
        var right = RenderSegmentRun(rightSegments);

        if (right.Length == 0)
        {
            return left;
        }

        var visibleLeft = StyledText.GetVisibleLength(left);
        var visibleRight = StyledText.GetVisibleLength(right);
        var available = Math.Max(0, width);
        var safeAvailable = available > 1 ? available - 1 : available;

        if (safeAvailable > visibleLeft + visibleRight + 1)
        {
            return left + new string(' ', safeAvailable - visibleLeft - visibleRight) + right;
        }

        if (left.Length == 0)
        {
            return right;
        }

        return left + " " + right;
    }

    private static string RenderSegmentRun(IReadOnlyList<object?> segments)
    {
        var run = new List<object?>();

        foreach (var segment in segments)
        {
            if (segment is null)
            {
                continue;
            }

            if (run.Count > 0)
            {
                run.Add(new StyledText(" "));
            }

            run.Add(segment);
        }

        return StyledText.RenderSegments(run);
    }

    private static IReadOnlyList<string> ParseLayout(string layout)
    {
        if (string.IsNullOrWhiteSpace(layout))
        {
            return Array.Empty<string>();
        }

        return layout.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static string NormalizeModuleName(string name)
    {
        return name.Trim().ToLowerInvariant() switch
        {
            "time" or "clock" => "Time",
            "dir" or "directory" or "cwd" or "pwd" => "Directory",
            "git" => "Git",
            "userhost" or "user-host" or "user@host" or "host" => "UserHost",
            "history" or "historyid" or "history-id" or "historynumber" or "history-number" or "event" or "eventid" or "event-id" => "HistoryId",
            "jobs" or "jobcount" => "Jobs",
            "duration" or "exec-time" or "elapsed" => "Duration",
            "exitcode" or "exit-code" or "status" => "ExitCode",
            "name" => "Name",
            "indicator" or "char" => "Indicator",
            _ => name.Trim(),
        };
    }

    private static bool IsSupportedModule(string name)
    {
        var normalized = NormalizeModuleName(name);
        return SupportedModuleNames.Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }

    private static PromptRenderContext CreateLiveContext(ToshRuntime runtime)
    {
        return new PromptRenderContext(
            runtime.CurrentDirectory,
            runtime.LastExitCode,
            runtime.LastCommandDuration,
            runtime.NextHistoryId,
            runtime.GetJobs().Count,
            Environment.UserName,
            UnixSystemServices.GetHostName(),
            DateTimeOffset.Now);
    }

    private static PromptRenderContext CreatePreviewContext(ToshRuntime runtime, int exitCode)
    {
        return new PromptRenderContext(
            runtime.CurrentDirectory,
            exitCode,
            TimeSpan.FromMilliseconds(1400),
            432,
            2,
            Environment.UserName,
            UnixSystemServices.GetHostName(),
            DateTimeOffset.Now);
    }

    private static int GetConsoleWidth()
    {
        try
        {
            if (Console.BufferWidth > 0)
            {
                return Console.BufferWidth;
            }
        }
        catch
        {
        }

        try
        {
            if (Console.WindowWidth > 0)
            {
                return Console.WindowWidth;
            }
        }
        catch
        {
        }

        return 80;
    }

    private readonly record struct PromptRenderResult(IReadOnlyList<string> Lines);

    private readonly record struct PromptRenderContext(
        string CurrentDirectory,
        int ExitCode,
        TimeSpan? LastDuration,
        long NextHistoryId,
        int JobCount,
        string UserName,
        string HostName,
        DateTimeOffset Now);
}
