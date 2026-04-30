namespace Tosh.Core.Commands;

[Stdlib(StdlibCategory.Filesystem)]
[CommandCategory("Filesystem")]
[CommandArgument("handle ...", "One or more managed file handles to close.", Required = false)]
[CommandExample("close $handle")]
[CommandExample("echo $handle | close")]
[CommandOutput("Closes the handles and does not emit pipeline output.")]
[PipelineInput(AcceptsRecord = true, Description = "Consumes piped file handles when explicit handles are omitted.")]
[CommandNote("These commands work with managed file handles returned by `open-file` or by `FileSystemEntry` methods like `OpenText()` and `OpenRead()`. `seek` returns the handle so you can keep piping through the stream workflow, while `copy-to` copies from one compatible handle into another.")]
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
