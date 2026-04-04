namespace Tosh.Core.Commands;

public sealed class LengthCommand : ShellCommand
{
    public LengthCommand()
        : base("length", "Returns the current stream length for one or more managed file handles.", "length [handle ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var handles = await StreamCommandUtilities.ResolveHandleListAsync(context);

        foreach (var handle in handles)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (handle.Length is not long length)
            {
                throw new InvalidOperationException("Length is not available for this handle.");
            }

            yield return length;
        }
    }
}
