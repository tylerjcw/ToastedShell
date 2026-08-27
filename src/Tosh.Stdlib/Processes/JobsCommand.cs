using Tosh.Runtime;

namespace Tosh.Stdlib.Processes;

[CommandCategory("Process")]
[CommandExample("jobs", Title = "List active background jobs")]
[CommandExample("jobs | get { Id, Status, CommandLine }", Title = "Project job fields")]
[CommandNote("Jobs lists ToSh background jobs started with a trailing `&`. A background launch updates `$tosh.Last.Result` with the started job info, while `jobs` and `wait-for` are the primary inspection commands.")]
[CommandOutput(
    "Job records describing each tracked background job: id, status, command line, and pid.",
    ClrType = typeof(IAsyncEnumerable<ShellJobInfo>))]
public sealed class JobsCommand : ShellCommand
{
    public JobsCommand()
        : base("jobs", "Lists ToSh background jobs.", "jobs") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        foreach (var job in context.Shell().GetJobs())
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            yield return job.ToInfo();
        }
    }
}
