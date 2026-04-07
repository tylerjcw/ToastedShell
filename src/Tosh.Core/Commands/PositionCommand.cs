namespace Tosh.Core.Commands;

[CommandCategory("Filesystem")]
public sealed class PositionCommand : ShellCommand
{
    public PositionCommand()
        : base("position", "Returns the current stream position for one or more managed file handles.", "position [handle ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var handles = await StreamCommandUtilities.ResolveHandleListAsync(context);

        foreach (var handle in handles)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (handle.Position is not long position)
            {
                throw new InvalidOperationException("Position is not available for this handle. Text reader handles do not expose a stable current byte position because .NET buffers decoded text.");
            }

            yield return position;
        }
    }
}
