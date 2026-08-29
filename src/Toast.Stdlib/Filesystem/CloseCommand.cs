using Tosh.Runtime;

namespace Tosh.Stdlib.Filesystem;

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
                // `TOAST-0007`. This named `HttpFileServerHandle`, so a language-level command
                // reached into the shell's network feature for a handle it only ever disposed.
                // Anything closeable is closeable; the specific case above still wins for a
                // file handle, which has its own `Close`.
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
                default:
                    throw new InvalidOperationException("Expected a file handle or another closeable value.");
            }
        }

        yield break;
    }
}
