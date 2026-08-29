using Tosh.Runtime;

namespace Tosh.Stdlib.Filesystem;

[CommandCategory("Filesystem")]
[CommandArgument("handle ...", "One or more managed file handles to flush.", Required = false)]
[CommandExample("flush $handle")]
[CommandExample("echo $handle | flush")]
[CommandOutput("Flushes the handles and does not emit pipeline output.")]
[PipelineInput(AcceptsRecord = true, Description = "Consumes piped file handles when explicit handles are omitted.")]
[CommandNote("These commands work with managed file handles returned by `open-file` or by `FileSystemEntry` methods like `OpenText()` and `OpenRead()`. `seek` returns the handle so you can keep piping through the stream workflow, while `copy-to` copies from one compatible handle into another.")]
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
