namespace Tosh.Core.Commands;

[CommandCategory("Prompt")]
public sealed class StyledCommand : ShellCommand
{
    public StyledCommand()
        : base("styled", "Creates a styled text segment with color and formatting.", "styled <text> [--fg color] [--bg color] [--bold] [--italic] [--underline] [--dim]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        await Task.CompletedTask;

        string? text = null;
        string? fg = null;
        string? bg = null;
        var bold = false;
        var italic = false;
        var underline = false;
        var dim = false;

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
                case "--italic":
                    italic = true;
                    break;
                case "--underline":
                    underline = true;
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

        yield return new StyledText(text, fg, bg, bold, italic, underline, dim);
    }
}