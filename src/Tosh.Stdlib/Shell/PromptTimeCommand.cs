using Tosh.Runtime;

namespace Tosh.Stdlib.Shell;

[ShellOnly]
[Stdlib(StdlibCategory.Shell)]
[CommandCategory("Prompt")]
[CommandOption("--fg <color>", "Foreground color name or ANSI-style color token.")]
[CommandOption("--bg <color>", "Background color name or ANSI-style color token.")]
[CommandOption("--bold", "Render the segment in bold.")]
[CommandOption("--dim", "Render the segment dimmed.")]
[CommandOption("--format <pattern>", "Date/time format string used to render the current time.")]
[CommandExample("prompt-time --dim")]
[CommandExample("prompt-time --format \"HH:mm:ss\" --fg gray")]
[CommandOutput("Styled prompt segment(s) showing the current wall-clock time.")]
public sealed class PromptTimeCommand : ShellCommand
{
    public PromptTimeCommand()
        : base("prompt-time", "Returns the current time as a styled prompt segment.", "prompt-time [--fg color] [--bg color] [--bold] [--dim] [--format pattern]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        await Task.CompletedTask;

        string? fg = null;
        string? bg = null;
        string? format = null;
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
                case "--format" when i + 1 < context.Arguments.Count:
                    format = context.Arguments[++i]?.ToString();
                    break;
                case "--bold":
                    bold = true;
                    break;
                case "--dim":
                    dim = true;
                    break;
            }
        }

        yield return PromptSegmentUtilities.BuildTimeSegment(DateTimeOffset.Now, format, fg, bg, bold, dim: dim);
    }
}
