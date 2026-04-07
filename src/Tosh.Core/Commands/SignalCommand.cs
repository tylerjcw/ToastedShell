namespace Tosh.Core.Commands;

[CommandCategory("Process")]
public sealed class SignalCommand : ShellCommand
{
    public SignalCommand()
        : base("signal", "Sends a signal to a ToSh background job or operating-system process.", "signal <signal> <job-id|pid> [job-id|pid ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count == 0)
        {
            throw new InvalidOperationException("signal requires a signal name or number.");
        }

        if (!ProcessSignalSender.TryParseSignal(context.Arguments[0], out var signal, out var displayName))
        {
            throw new InvalidOperationException($"'{context.Arguments[0]}' is not a supported signal name or number.");
        }

        var targets = new List<object?>();

        for (var index = 1; index < context.Arguments.Count; index++)
        {
            targets.Add(context.Arguments[index]);
        }

        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            targets.Add(item);
        }

        if (targets.Count == 0)
        {
            throw new InvalidOperationException("signal requires at least one job id, process id, or pipeline input.");
        }

        foreach (var target in targets)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            yield return SendSignal(context.Runtime, signal, displayName, target);
        }

        context.Runtime.SetLastExitCode(0);
    }

    private static JobControlResult SendSignal(ToshRuntime runtime, int signal, string displayName, object? target)
    {
        if (TryResolveJob(runtime, target, out var job))
        {
            var sent = job!.SendSignal(signal, out var error);
            return new JobControlResult(
                "signal",
                job.Id,
                job.ProcessId,
                sent,
                sent
                    ? $"Sent {displayName} to background job [{job.Id}]."
                    : $"Failed to send {displayName} to background job [{job.Id}]: {error}")
            {
                Status = job.Status,
            };
        }

        if (TryResolveProcessId(target, out var processId))
        {
            var sent = ProcessSignalSender.TrySend(processId, signal, out var error);
            return new JobControlResult(
                "signal",
                null,
                processId,
                sent,
                sent
                    ? $"Sent {displayName} to process {processId}."
                    : $"Failed to send {displayName} to process {processId}: {error}");
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
