namespace Tosh.Core.Commands;

[ShellOnly]
[Stdlib(StdlibCategory.Shell)]
[CommandCategory("Prompt")]
[CommandArgument("count", "Job count to render instead of the shell's live background-job count.", Required = false, TypeName = "int")]
[CommandOption("--fg <color>", "Foreground color name or ANSI-style color token.")]
[CommandOption("--bg <color>", "Background color name or ANSI-style color token.")]
[CommandOption("--bold", "Render the segment in bold.")]
[CommandOption("--dim", "Render the segment dimmed.")]
[CommandExample("prompt-jobs")]
[CommandExample("prompt-jobs 3 --fg yellow --bold")]
[CommandOutput("Styled prompt segment(s) summarising background-job counts.")]
public sealed class PromptJobsCommand : ShellCommand
{
    public PromptJobsCommand()
        : base("prompt-jobs", "Returns the current background job count as a styled prompt segment.", "prompt-jobs [count] [--fg color] [--bg color] [--bold] [--dim]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        await Task.CompletedTask;

        string? fg = null;
        string? bg = null;
        int? jobCount = null;
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
                    jobCount ??= CommandArguments.RequireConverted<int>(context.Arguments, i, "count");
                    break;
            }
        }

        var effectiveCount = jobCount ?? context.Runtime.GetJobs().Count;

        if (effectiveCount <= 0)
        {
            yield break;
        }

        yield return new StyledText($"jobs:{effectiveCount}", fg, bg, bold, Dim: dim);
    }
}
