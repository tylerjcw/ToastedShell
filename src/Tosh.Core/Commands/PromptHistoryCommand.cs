namespace Tosh.Core.Commands;

[CommandCategory("Prompt")]
[CommandExample("prompt-history")]
[CommandExample("prompt-history 432 --fg gray --dim")]
public sealed class PromptHistoryCommand : ShellCommand
{
    public PromptHistoryCommand()
        : base("prompt-history", "Returns the next history id as a styled prompt segment.", "prompt-history [id] [--fg color] [--bg color] [--bold] [--dim]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        await Task.CompletedTask;

        string? fg = null;
        string? bg = null;
        long? historyId = null;
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
                    historyId ??= CommandArguments.RequireConverted<long>(context.Arguments, i, "id");
                    break;
            }
        }

        yield return PromptSegmentUtilities.BuildHistoryIdSegment(historyId ?? context.Runtime.NextHistoryId, fg, bg, bold, dim: dim);
    }
}
