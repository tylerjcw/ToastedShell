using Tosh.Runtime;

namespace Tosh.Stdlib.Shell;

/// <summary>
/// Consolidated entry point for prompt segment builders.
/// Delegates to one of the legacy <c>prompt-*</c> command implementations
/// based on the first positional argument. The legacy commands remain
/// individually registered so existing profiles, scripts, and
/// <c>help prompt-*</c> queries keep working unchanged; once the
/// fading-alias mechanism lands (backlog #3.4) they can be demoted to
/// soft-deprecated aliases for one major.
/// </summary>
[ShellOnly]
[CommandCategory("Prompt")]
[CommandArgument("segment", "Segment kind. One of: time, dir, git, userhost, history, jobs, duration, exit, text, newline.")]
[CommandArgument("args ...", "Remaining arguments forwarded to the chosen segment builder (segment-specific options, e.g. --fg, --bg, --bold, --dim, plus per-segment flags).", Required = false)]
[CommandExample("prompt time --format HH:mm --dim")]
[CommandExample("prompt dir --depth 2 --fg cyan")]
[CommandExample("prompt git --bold")]
[CommandExample("prompt text \" » \" --fg gray")]
[CommandOutput("Styled prompt segment(s) produced by the selected segment builder.")]
[CommandNote("Each subcommand corresponds to a legacy `prompt-<segment>` command. The legacy names remain available for now; use `help prompt-<segment>` (e.g. `help prompt-time`) for the per-segment option reference.")]
public sealed class PromptCommand : ShellCommand
{
    private readonly Dictionary<string, IShellCommand> _segments;

    public PromptCommand()
        : base(
            "prompt",
            "Returns a styled prompt segment. Dispatches to the named segment builder (time, dir, git, userhost, history, jobs, duration, exit, text, newline).",
            "prompt <segment> [args ...]")
    {
        _segments = new Dictionary<string, IShellCommand>(StringComparer.OrdinalIgnoreCase)
        {
            ["time"] = new PromptTimeCommand(),
            ["dir"] = new PromptDirCommand(),
            ["git"] = new PromptGitCommand(),
            ["userhost"] = new PromptUserHostCommand(),
            ["history"] = new PromptHistoryCommand(),
            ["jobs"] = new PromptJobsCommand(),
            ["duration"] = new PromptDurationCommand(),
            ["exit"] = new PromptExitCodeCommand(),
            ["text"] = new PromptTextCommand(),
            ["newline"] = new PromptNewlineCommand(),
        };
    }

    public override IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count == 0)
        {
            throw context.CreateDiagnostic(
                code: "tosh.command.missing_subcommand",
                title: "'prompt' requires a segment name.",
                help: "Available segments: time, dir, git, userhost, history, jobs, duration, exit, text, newline. Example: `prompt time --dim`.");
        }

        var subcommand = context.Arguments[0]?.ToString();
        if (string.IsNullOrEmpty(subcommand) || !_segments.TryGetValue(subcommand, out var impl))
        {
            throw context.CreateDiagnostic(
                code: "tosh.command.unknown_subcommand",
                title: $"'prompt {subcommand}' is not a recognized segment.",
                argumentIndex: 0,
                help: "Available segments: time, dir, git, userhost, history, jobs, duration, exit, text, newline.");
        }

        var forwardedArgs = new List<object?>(context.Arguments.Count - 1);
        for (var i = 1; i < context.Arguments.Count; i++)
        {
            forwardedArgs.Add(context.Arguments[i]);
        }

        return impl.ExecuteAsync(context with { Arguments = forwardedArgs });
    }
}
