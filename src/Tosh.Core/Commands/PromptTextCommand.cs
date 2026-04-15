namespace Tosh.Core.Commands;

[CommandCategory("Prompt")]
[CommandExample("prompt-text \"> \" --fg cyan")]
[CommandExample("prompt-text \"::\" --fg gray --dim")]
public sealed class PromptTextCommand : ShellCommand
{
    public PromptTextCommand()
        : base("prompt-text", "Returns literal text as a styled prompt segment.", "prompt-text <text> [--fg color] [--bg color] [--bold] [--dim]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        await Task.CompletedTask;

        string? text = null;
        string? fg = null;
        string? bg = null;
        var bold = false;
        var dim = false;

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
                case "--dim":
                    dim = true;
                    break;
                default:
                    text ??= arg;
                    break;
            }
        }

        if (text is null)
        {
            yield break;
        }

        yield return new StyledText(text, fg, bg, bold, Dim: dim);
    }
}