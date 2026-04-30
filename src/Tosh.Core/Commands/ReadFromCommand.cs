namespace Tosh.Core.Commands;

[Stdlib(StdlibCategory.Filesystem)]
[CommandCategory("Filesystem")]
[CommandArgument("handle", "The managed file handle to read from.", Required = false)]
[CommandArgument("count", "Optional chunk size. Defaults to {StreamCommandUtilities.DefaultReadChunkSize}.", Required = false, TypeName = "int")]
[CommandExample("$handle | read-from 64")]
[CommandExample("read-from $handle 4096")]
[CommandOutput("Returns a string chunk for text handles or a byte array chunk for binary handles.")]
[PipelineInput(AcceptsRecord = true, Description = "Consumes a piped file handle when no explicit handle argument is supplied.")]
[CommandNote("These commands work with managed file handles returned by `open-file` or by `FileSystemEntry` methods like `OpenText()` and `OpenRead()`. `seek` returns the handle so you can keep piping through the stream workflow, while `copy-to` copies from one compatible handle into another.")]
public sealed class ReadFromCommand : ShellCommand
{
    public ReadFromCommand(string name = "read-from")
        : base(name, "Reads a text or binary chunk from an open managed file handle.", $"{name} [handle] [count]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var (handle, count) = await StreamCommandUtilities.ResolveSingleReadableHandleAsync(context);
        var readCount = count ?? StreamCommandUtilities.DefaultReadChunkSize;

        if (handle.IsBinary)
        {
            var bytes = handle.ReadBytes(readCount);

            if (bytes.Length > 0)
            {
                yield return bytes;
            }

            yield break;
        }

        var text = handle.ReadText(readCount);

        if (text.Length > 0)
        {
            yield return text;
        }
    }
}
