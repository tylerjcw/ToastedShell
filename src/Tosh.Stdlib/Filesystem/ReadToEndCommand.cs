using Tosh.Runtime;

namespace Tosh.Stdlib.Filesystem;

[CommandCategory("Filesystem")]
[CommandArgument("handle", "The managed file handle to read from.", Required = false)]
[CommandExample("$handle | read-to-end")]
[CommandExample("read-to-end $handle")]
[CommandOutput("Returns the remaining text or bytes from the handle.")]
[PipelineInput(AcceptsRecord = true, Description = "Consumes a piped file handle when no explicit handle argument is supplied.")]
[CommandNote("These commands work with managed file handles returned by `open-file` or by `FileSystemEntry` methods like `OpenText()` and `OpenRead()`. `seek` returns the handle so you can keep piping through the stream workflow, while `copy-to` copies from one compatible handle into another.")]
public sealed class ReadToEndCommand : ShellCommand
{
    public ReadToEndCommand(string name = "read-to-end")
        : base(name, "Reads the remainder of an open managed file handle.", $"{name} [handle]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var (handle, _) = await StreamCommandUtilities.ResolveSingleReadableHandleAsync(context);

        if (handle.IsBinary)
        {
            yield return handle.ReadToEndBytes();
            yield break;
        }

        yield return handle.ReadToEndText();
    }
}
