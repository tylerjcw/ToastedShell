namespace Tosh.Core.Commands;

[CommandCategory("Prompt")]
[CommandArgument("duration", "Duration to render instead of the last command duration.", Required = false)]
[CommandOption("--fg <color>", "Foreground color name or ANSI-style color token.")]
[CommandOption("--bg <color>", "Background color name or ANSI-style color token.")]
[CommandOption("--bold", "Render the segment in bold.")]
[CommandOption("--dim", "Render the segment dimmed.")]
[CommandOption("--threshold-ms <value>", "Suppress the segment unless duration is at least this many milliseconds.")]
[CommandExample("prompt-duration")]
[CommandExample("prompt-duration 2.5s --threshold-ms 250")]
public sealed class PromptDurationCommand : ShellCommand
{
    public PromptDurationCommand()
        : base("prompt-duration", "Returns the last command duration as a styled prompt segment.", "prompt-duration [duration] [--fg color] [--bg color] [--bold] [--dim] [--threshold-ms value]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        await Task.CompletedTask;

        string? fg = null;
        string? bg = null;
        TimeSpan? duration = null;
        var threshold = TimeSpan.Zero;
        var bold = false;
        var dim = false;

        for (var i = 0; i < context.Arguments.Count; i++)
        {
            var arg = context.Arguments[i]?.ToString() ?? string.Empty;

            switch (arg)
            {
                case "--fg" when i + 1 < context.Arguments.Count:
                    fg = context.Arguments[++i]?.ToString();
                    break;
                case "--bg" when i + 1 < context.Arguments.Count:
                    bg = context.Arguments[++i]?.ToString();
                    break;
                case "--threshold-ms" when i + 1 < context.Arguments.Count:
                    threshold = TimeSpan.FromMilliseconds(CommandArguments.RequireConverted<int>(context.Arguments, ++i, "threshold-ms"));
                    break;
                case "--bold":
                    bold = true;
                    break;
                case "--dim":
                    dim = true;
                    break;
                default:
                    if (!TypeConversion.TryConvert(context.Arguments[i], typeof(TimeSpan), out var converted) || converted is not TimeSpan parsedDuration)
                    {
                        throw new InvalidOperationException($"Could not convert {arg} to a duration.");
                    }

                    duration ??= parsedDuration;
                    break;
            }
        }

        var segment = PromptSegmentUtilities.BuildDurationSegment(
            duration ?? context.Runtime.LastCommandDuration,
            threshold,
            new ToshTextStyleConfig(foreground: fg, background: bg, bold: bold, dim: dim));

        if (segment is not null)
        {
            yield return segment;
        }
    }
}
