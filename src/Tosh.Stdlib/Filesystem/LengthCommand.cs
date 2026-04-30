using Tosh.Runtime;

namespace Tosh.Stdlib.Filesystem;

[Stdlib(StdlibCategory.Filesystem)]
[CommandCategory("Filesystem")]
[CommandArgument("handle ...", "One or more managed file handles whose current stream length should be reported.", Required = false)]
[CommandExample("length $handle")]
[CommandExample("echo $handle | length")]
[CommandOutput("Returns the current underlying stream length for each supplied handle.")]
[PipelineInput(AcceptsRecord = true, Description = "Consumes piped file handles when explicit handles are omitted.")]
[CommandNote("These commands work with managed file handles returned by `open-file` or by `FileSystemEntry` methods like `OpenText()` and `OpenRead()`. `seek` returns the handle so you can keep piping through the stream workflow, while `copy-to` copies from one compatible handle into another.")]
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
