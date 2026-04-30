using Tosh.Runtime;

namespace Tosh.Stdlib.Shell;

[ShellOnly]
[CommandCategory("Prompt")]
[CommandOption("--fg <color>", "Foreground color name or ANSI-style color token.")]
[CommandOption("--bg <color>", "Background color name or ANSI-style color token.")]
[CommandOption("--bold", "Render the segment in bold.")]
[CommandOption("--dim", "Render the segment dimmed.")]
[CommandExample("prompt-userhost --dim")]
[CommandExample("prompt-userhost --fg gray")]
[CommandOutput("Styled prompt segment(s) describing the current user@host.")]
public sealed class PromptUserHostCommand : ShellCommand
{
    public PromptUserHostCommand()
        : base("prompt-userhost", "Returns the current user and host as a styled prompt segment.", "prompt-userhost [--fg color] [--bg color] [--bold] [--dim]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        await Task.CompletedTask;

        string? fg = null;
        string? bg = null;
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
            }
        }

        yield return new StyledText($"{Environment.UserName}@{UnixSystemServices.GetHostName()}", fg, bg, bold, Dim: dim);
    }
}
