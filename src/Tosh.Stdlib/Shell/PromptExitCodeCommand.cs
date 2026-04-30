using Tosh.Runtime;

namespace Tosh.Stdlib.Shell;

[ShellOnly]
[Stdlib(StdlibCategory.Shell)]
[CommandCategory("Prompt")]
[CommandArgument("code", "Exit code to render instead of the shell's last exit code.", Required = false, TypeName = "int")]
[CommandOption("--fg <color>", "Foreground color name or ANSI-style color token.")]
[CommandOption("--bg <color>", "Background color name or ANSI-style color token.")]
[CommandOption("--bold", "Render the segment in bold.")]
[CommandOption("--dim", "Render the segment dimmed.")]
[CommandExample("prompt-exit")]
[CommandExample("prompt-exit 7 --fg red --bold")]
[CommandOutput("Styled prompt segment(s) describing the previous command's exit status.")]
public sealed class PromptExitCodeCommand : ShellCommand
{
    public PromptExitCodeCommand()
        : base("prompt-exit", "Returns the last non-zero exit code as a styled prompt segment.", "prompt-exit [code] [--fg color] [--bg color] [--bold] [--dim]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        await Task.CompletedTask;

        string? fg = null;
        string? bg = null;
        int? exitCode = null;
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
                case "--bold":
                    bold = true;
                    break;
                case "--dim":
                    dim = true;
                    break;
                default:
                    if (!TypeConversion.TryConvert(arg, typeof(int), out var converted) || converted is not int parsedExitCode)
                    {
                        throw new InvalidOperationException($"Could not convert {arg} to an exit code.");
                    }

                    exitCode ??= parsedExitCode;
                    break;
            }
        }

        var segment = PromptSegmentUtilities.BuildExitCodeSegment(exitCode ?? context.Runtime.LastExitCode, fg, bg, bold, dim: dim);

        if (segment is not null)
        {
            yield return segment;
        }
    }
}
