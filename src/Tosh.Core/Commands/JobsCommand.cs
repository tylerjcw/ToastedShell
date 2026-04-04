namespace Tosh.Core.Commands;

public sealed class JobsCommand : ShellCommand
{
    public JobsCommand()
        : base("jobs", "Lists ToSh background jobs.", "jobs") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        foreach (var job in context.Runtime.GetJobs())
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            yield return job.ToInfo();
        }
    }
}
