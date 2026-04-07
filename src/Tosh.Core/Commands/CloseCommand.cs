namespace Tosh.Core.Commands;

[CommandCategory("Filesystem")]
public sealed class CloseCommand : ShellCommand
{
    public CloseCommand()
        : base("close", "Closes one or more managed file or server handles.", "close <handle> [handle...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var values = context.Arguments.Count > 0
            ? context.Arguments
            : await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);

        if (values.Count == 0)
        {
            throw new InvalidOperationException("This command expects one or more file or server handles.");
        }

        foreach (var handle in values)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            switch (handle)
            {
                case ManagedFileHandle fileHandle:
                    fileHandle.Close();
                    break;
                case HttpFileServerHandle serverHandle:
                    serverHandle.Close();
                    break;
                default:
                    throw new InvalidOperationException("Expected a file handle or HTTP server handle value.");
            }
        }

        yield break;
    }
}
