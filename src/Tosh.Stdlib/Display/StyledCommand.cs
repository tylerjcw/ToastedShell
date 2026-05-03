using Tosh.Runtime;

namespace Tosh.Stdlib.Display;

[CommandCategory("Prompt")]
[CommandArgument("text", "Text to wrap in a StyledText value.")]
[CommandOption("--fg <color>", "Foreground color name or ANSI-style color token.")]
[CommandOption("--bg <color>", "Background color name or ANSI-style color token.")]
[CommandOption("--bold", "Render the segment in bold.")]
[CommandOption("--italic", "Render the segment in italic.")]
[CommandOption("--underline", "Render the segment underlined.")]
[CommandOption("--dim", "Render the segment dimmed.")]
[CommandExample("styled \"hello\" --fg cyan --bold")]
[CommandExample("styled \"warning\" --fg yellow --bg red")]
[CommandOutput("Styled-text values that carry inline color/format markup for downstream prompts and renderers.", ClrType = typeof(IAsyncEnumerable<StyledText>))]
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
