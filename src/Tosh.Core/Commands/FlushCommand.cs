namespace Tosh.Core.Commands;

[CommandCategory("Filesystem")]
public sealed class FlushCommand : ShellCommand
{
    public FlushCommand()
        : base("flush", "Flushes one or more managed file handles.", "flush <handle> [handle...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var handles = await StreamCommandUtilities.ResolveHandleListAsync(context);

        foreach (var handle in handles)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            handle.Flush();
        }

        yield break;
    }
}
