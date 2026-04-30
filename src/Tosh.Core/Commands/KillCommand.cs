using System.Diagnostics;

namespace Tosh.Core.Commands;

[Stdlib(StdlibCategory.Process)]
[CommandCategory("Process")]
[CommandArgument("job-id|pid ...", "One or more ToSh background job ids, ShellJobInfo values, ProcessInfo values, or native process ids.")]
[CommandExample("sleep 60 &; jobs | first | kill", Title = "Kill a background job from the pipeline")]
[CommandExample("kill 12345", Title = "Kill a native process id")]
[CommandSideEffects(SpawnsProcess = true)]
[CommandNote("Kill can stop either a ToSh background job or a native operating-system process by pid.")]
[CommandOutput("Emits nothing; sends the requested signal to the targeted process(es) as a side effect.")]
public sealed class KillCommand : ShellCommand
{
    public KillCommand()
        : base("kill", "Stops a ToSh background job or operating-system process.", "kill <job-id|pid> [job-id|pid ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var targets = new List<object?>();
        targets.AddRange(context.Arguments);

        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            targets.Add(item);
        }

        if (targets.Count == 0)
        {
            throw new InvalidOperationException("kill requires at least one job id, process id, or pipeline input.");
        }

        foreach (var target in targets)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            yield return KillTarget(context.Runtime, target);
        }

        context.Runtime.SetLastExitCode(0);
    }

    private static JobControlResult KillTarget(ToshRuntime runtime, object? target)
    {
        if (TryResolveJob(runtime, target, out var job))
        {
            var killed = job!.Kill();
            return new JobControlResult(
                "kill",
                job.Id,
                job.ProcessId,
                killed,
                killed ? $"Termination requested for background job [{job.Id}]." : $"Background job [{job.Id}] is not running.")
            {
                Status = job.Status,
            };
        }

        if (TryResolveProcessId(target, out var processId))
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                process.Kill(entireProcessTree: true);
                return new JobControlResult(
                    "kill",
                    null,
                    processId,
                    true,
                    $"Termination requested for process {processId}.")
                {
                    Status = ShellJobStatus.Cancelled,
                };
            }
            catch (Exception exception)
            {
                return new JobControlResult(
                    "kill",
                    null,
                    processId,
                    false,
                    exception.Message);
            }
        }

        throw new InvalidOperationException($"'{target}' is not a valid job or process reference.");
    }

    private static bool TryResolveJob(ToshRuntime runtime, object? target, out ShellJob? job)
    {
        switch (target)
        {
            case ShellJobInfo info when runtime.TryGetJob(info.Id, out var jobFromInfo):
                job = jobFromInfo;
                return true;
            case ShellJobCompletion completion when runtime.TryGetJob(completion.Id, out var jobFromCompletion):
                job = jobFromCompletion;
                return true;
            case int id when runtime.TryGetJob(id, out var jobFromInt):
                job = jobFromInt;
                return true;
            case long longId when longId is >= int.MinValue and <= int.MaxValue && runtime.TryGetJob((int)longId, out var jobFromLong):
                job = jobFromLong;
                return true;
            case string text when int.TryParse(text, out var parsedId) && runtime.TryGetJob(parsedId, out var jobFromText):
                job = jobFromText;
                return true;
            default:
                job = null;
                return false;
        }
    }

    private static bool TryResolveProcessId(object? target, out int processId)
    {
        switch (target)
        {
            case ProcessInfo process:
                processId = process.Id;
                return true;
            case int id:
                processId = id;
                return true;
            case long longId when longId is >= int.MinValue and <= int.MaxValue:
                processId = (int)longId;
                return true;
            case string text when int.TryParse(text, out var parsed):
                processId = parsed;
                return true;
            default:
                processId = 0;
                return false;
        }
    }
}
