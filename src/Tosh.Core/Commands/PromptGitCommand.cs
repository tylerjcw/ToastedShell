namespace Tosh.Core.Commands;

public sealed class PromptGitCommand : ShellCommand
{
    public PromptGitCommand()
        : base("prompt-git", "Returns git branch and status as styled prompt segments.", "prompt-git [--fg color] [--bg color] [--bold]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        await Task.CompletedTask;

        string? fg = "green";
        string? bg = null;
        var bold = false;

        for (var i = 0; i < context.Arguments.Count; i++)
        {
            var arg = context.Arguments[i]?.ToString() ?? "";

            switch (arg)
            {
                case "--fg" when i + 1 < context.Arguments.Count:
                    fg = context.Arguments[++i]?.ToString() ?? "";
                    break;
                case "--bg" when i + 1 < context.Arguments.Count:
                    bg = context.Arguments[++i]?.ToString() ?? "";
                    break;
                case "--bold":
                    bold = true;
                    break;
            }
        }

        var segment = PromptSegmentUtilities.BuildGitSegment(context.Runtime.CurrentDirectory, fg, bg, bold);

        if (segment is not null)
        {
            yield return segment;
        }
    }
}
