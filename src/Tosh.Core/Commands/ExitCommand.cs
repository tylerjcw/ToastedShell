namespace Tosh.Core.Commands;

[Stdlib(StdlibCategory.Shell)]
[CommandCategory("Shell")]
[CommandExample("exit", Title = "Exit the current session")]
[CommandExample("exit 1", Title = "Exit with a specific exit code")]
[CommandNote("In a login shell, `exit` behaves like `logout`: it warns about running background jobs (use `exit` again to force), and sources ~/.config/tosh/logout.tosh before terminating.")]
[CommandOutput("No output. The session terminates.")]
[PipelineInput(Description = "Not applicable.")]
public sealed class ExitCommand : ShellCommand
{
    public ExitCommand(string name = "exit")
        : base(name, "Requests the current Tosh session to exit.", $"{name} [code]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        // Parse optional exit code.
        if (context.Arguments.Count > 0
            && context.Arguments[0] is string codeStr
            && int.TryParse(codeStr, out var exitCode))
        {
            context.Runtime.SetLastExitCode(exitCode);
        }

        // Background job warning (like bash/zsh): first exit warns, second exit forces.
        var runningJobs = context.Runtime.GetJobs();
        if (runningJobs.Count > 0 && !context.Runtime.ExitWarningIssued)
        {
            context.Runtime.ExitWarningIssued = true;
            await Console.Error.WriteLineAsync(
                $"tosh: there {(runningJobs.Count == 1 ? "is" : "are")} {runningJobs.Count} running job{(runningJobs.Count == 1 ? "" : "s")}. Use '{context.Invocation?.CommandName ?? "exit"}' again to force.");
            yield break;
        }

        context.Runtime.RequestExit();
        yield break;
    }
}
