namespace Tosh.Core.Commands.Processes;

[Stdlib(StdlibCategory.Process)]
[CommandCategory("Process")]
[CommandArgument("id", "The id of the suspended job to bring to the foreground.", Required = false, TypeName = "int")]
[CommandNote("Resumes a suspended job (stopped with Ctrl+Z) in the foreground. If no id is given, the most recently suspended job is used.")]
[CommandExample("fg", Title = "Resume the most recently suspended job.")]
[CommandExample("fg 2", Title = "Resume suspended job 2.")]
[CommandOutput("Emits nothing; brings the targeted job into the foreground as a side effect.")]
public sealed class ForegroundCommand : ShellCommand
{
    public ForegroundCommand()
        : base("fg", "Resumes a suspended job in the foreground.", "fg [id]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var terminal = context.Runtime.Terminal;
        ShellJob? job;

        if (context.Arguments.Count > 0)
        {
            if (!TryResolveJob(context.Runtime, context.Arguments[0], out job))
            {
                throw new InvalidOperationException($"'{context.Arguments[0]}' is not a valid job reference.");
            }
        }
        else
        {
            job = FindMostRecentSuspended(context.Runtime);

            if (job is null)
            {
                throw new InvalidOperationException("No suspended jobs.");
            }
        }

        var result = job!.TryResumeForeground(terminal, out var error);

        if (error is not null)
        {
            throw new InvalidOperationException(error);
        }

        switch (result.Outcome)
        {
            case ForegroundWaitOutcome.Exited:
                context.Runtime.SetLastExitCode(result.StatusOrSignal);
                break;

            case ForegroundWaitOutcome.Stopped:
                // Re-suspended — print the stopped message again.
                context.Runtime.SetLastExitCode(148); // 128 + SIGTSTP(20)
                await context.Runtime.Error.WriteLineAsync(
                    $"[{job.Id}]  Stopped                 {job.Command}");
                break;

            default:
                context.Runtime.SetLastExitCode(job.ExitCode ?? 0);
                break;
        }

        yield return new JobControlResult(
            "fg",
            job.Id,
            job.ProcessId,
            true,
            result.Outcome == ForegroundWaitOutcome.Stopped
                ? $"Job [{job.Id}] suspended again."
                : $"Job [{job.Id}] completed.")
        {
            Status = job.Status,
        };
    }

    private static ShellJob? FindMostRecentSuspended(ToshRuntime runtime)
    {
        ShellJob? best = null;

        foreach (var job in runtime.GetJobs())
        {
            if (job.Status == ShellJobStatus.Suspended)
            {
                if (best is null || job.Id > best.Id)
                {
                    best = job;
                }
            }
        }

        return best;
    }

    private static bool TryResolveJob(ToshRuntime runtime, object? target, out ShellJob? job)
    {
        switch (target)
        {
            case ShellJobInfo info when runtime.TryGetJob(info.Id, out var j):
                job = j;
                return true;
            case int id when runtime.TryGetJob(id, out var j):
                job = j;
                return true;
            case long longId when longId is >= int.MinValue and <= int.MaxValue && runtime.TryGetJob((int)longId, out var j):
                job = j;
                return true;
            case string text when int.TryParse(text, out var parsedId) && runtime.TryGetJob(parsedId, out var j):
                job = j;
                return true;
            default:
                job = null;
                return false;
        }
    }
}
