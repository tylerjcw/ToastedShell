namespace Tosh.Core.Commands;

public sealed class CloseCommand : ShellCommand
{
    public CloseCommand()
        : base("close", "Closes one or more managed file handles.", "close <handle> [handle...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var handles = await StreamCommandUtilities.ResolveHandleListAsync(context);

        foreach (var handle in handles)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            handle.Close();
        }

        yield break;
    }
}
