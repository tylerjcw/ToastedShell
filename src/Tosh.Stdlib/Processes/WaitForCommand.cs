using Tosh.Runtime;

namespace Tosh.Stdlib.Processes;

[CommandCategory("Process")]
[CommandArgument("job-id ...", "Optional ToSh background job ids or job objects to await. With no targets, waits for all current jobs.", Required = false)]
[CommandExample("wait-for", Title = "Wait for all background jobs")]
[CommandExample("spawn git status | wait-for", Title = "Wait for a job supplied by the pipeline")]
[CommandNote("Wait-for blocks until one or more background jobs finish and returns structured completion objects.")]
[CommandOutput("Emits nothing on success; throws when the awaited condition does not become true within the timeout.")]
public sealed class WaitForCommand : ShellCommand
{
    public WaitForCommand()
        : base("wait-for", "Waits for one or more ToSh background jobs to finish.", "wait-for [job-id ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var jobs = await ResolveTargetJobsAsync(context);
        ShellJobCompletion? lastCompletion = null;

        foreach (var job in jobs)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            lastCompletion = await job.WaitAsync(context.CancellationToken);
            yield return lastCompletion;
        }

        if (lastCompletion is not null)
        {
            context.Shell().SetLastExitCode(lastCompletion.ExitCode ?? 0);
        }
    }

    private static async Task<IReadOnlyList<ShellJob>> ResolveTargetJobsAsync(CommandContext context)
    {
        var targets = new List<ShellJob>();
        var seen = new HashSet<int>();

        void AddJob(ShellJob job)
        {
            if (seen.Add(job.Id))
            {
                targets.Add(job);
            }
        }

        foreach (var argument in context.Arguments)
        {
            AddResolvedTarget(context.Shell(), argument, AddJob);
        }

        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            AddResolvedTarget(context.Shell(), item, AddJob);
        }

        if (targets.Count == 0)
        {
            foreach (var job in context.Shell().GetJobs())
            {
                AddJob(job);
            }
        }

        return targets;
    }

    private static void AddResolvedTarget(ToshRuntime runtime, object? value, Action<ShellJob> addJob)
    {
        switch (value)
        {
            case null:
                return;
            case ShellJobInfo info when runtime.TryGetJob(info.Id, out var jobFromInfo):
                addJob(jobFromInfo);
                return;
            case ShellJobCompletion completion when runtime.TryGetJob(completion.Id, out var jobFromCompletion):
                addJob(jobFromCompletion);
                return;
            case int id when runtime.TryGetJob(id, out var jobFromInt):
                addJob(jobFromInt);
                return;
            case long longId when longId is >= int.MinValue and <= int.MaxValue && runtime.TryGetJob((int)longId, out var jobFromLong):
                addJob(jobFromLong);
                return;
            case string text when int.TryParse(text, out var parsedId) && runtime.TryGetJob(parsedId, out var jobFromText):
                addJob(jobFromText);
                return;
            default:
                throw new InvalidOperationException($"'{value}' is not a valid ToSh job reference.");
        }
    }
}
