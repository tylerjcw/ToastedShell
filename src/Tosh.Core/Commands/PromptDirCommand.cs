namespace Tosh.Core.Commands;

public sealed class PromptDirCommand : ShellCommand
{
    public PromptDirCommand()
        : base("prompt-dir", "Returns the current directory as a styled prompt segment.", "prompt-dir [--fg color] [--bg color] [--bold] [--depth n]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        await Task.CompletedTask;

        string? fg = null;
        string? bg = null;
        var bold = false;
        int? depth = null;

        for (var i = 0; i < context.Arguments.Count; i++)
        {
            var arg = context.Arguments[i]?.ToString() ?? "";

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
                case "--depth" when i + 1 < context.Arguments.Count:
                    depth = CommandArguments.RequireConverted<int>(context.Arguments, ++i, "depth");
                    break;
            }
        }

        yield return PromptSegmentUtilities.BuildDirectorySegment(context.Runtime.CurrentDirectory, depth, fg, bg, bold);
    }
}
