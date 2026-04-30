using Tosh.Runtime;

namespace Tosh.Stdlib.Processes;

[CommandCategory("Process")]
[CommandArgument("id", "The id of the suspended job to resume in the background.", Required = false, TypeName = "int")]
[CommandNote("Resumes a suspended job in the background. The process receives SIGCONT but does not get the terminal, so it runs without interactive I/O. If no id is given, the most recently suspended job is used.")]
[CommandExample("bg", Title = "Resume the most recently suspended job in the background.")]
[CommandExample("bg 2", Title = "Resume suspended job 2 in the background.")]
[CommandOutput("Emits nothing; resumes the targeted job in the background as a side effect.")]
public sealed class BackgroundResumeCommand : ShellCommand
{
    public BackgroundResumeCommand()
        : base("bg", "Resumes a suspended job in the background.", "bg [id]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        _ = context.CancellationToken; // suppress unused parameter warning
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

        if (!job!.TryResumeBackground(out var error))
        {
            throw new InvalidOperationException(error ?? $"Failed to resume job [{job.Id}].");
        }

        await context.Runtime.Error.WriteLineAsync(
            $"[{job.Id}]  {job.Command} &");

        yield return new JobControlResult(
            "bg",
            job.Id,
            job.ProcessId,
            true,
            $"Job [{job.Id}] resumed in background.")
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
