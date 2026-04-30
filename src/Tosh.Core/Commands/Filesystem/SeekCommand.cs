using System.IO;

namespace Tosh.Core.Commands.Filesystem;

[Stdlib(StdlibCategory.Filesystem)]
[CommandCategory("Filesystem")]
[CommandArgument("handle", "The managed file handle to reposition.", Required = false)]
[CommandArgument("offset", "The byte offset to seek to or by.", TypeName = "long")]
[CommandArgument("origin", "The seek origin: begin, current, or end. Defaults to begin.", Required = false)]
[CommandExample("seek $handle 0 begin")]
[CommandExample("$handle | seek 128 current")]
[CommandOutput("Moves the handle and returns it so you can continue piping into `read-from`, `read-line-from`, `read-to-end`, or `copy-to`.")]
[PipelineInput(AcceptsRecord = true, Description = "Consumes a piped file handle when no explicit handle argument is supplied.")]
[CommandNote("These commands work with managed file handles returned by `open-file` or by `FileSystemEntry` methods like `OpenText()` and `OpenRead()`. `seek` returns the handle so you can keep piping through the stream workflow, while `copy-to` copies from one compatible handle into another.")]
public sealed class SeekCommand : ShellCommand
{
    public SeekCommand()
        : base("seek", "Moves an open managed file handle to a new position and returns the handle for continued piping.", "seek [handle] <offset> [begin|current|end]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var (handle, remainingArguments) = await StreamCommandUtilities.ResolveSingleHandleAndArgumentsAsync(context);

        if (remainingArguments.Count == 0)
        {
            throw new InvalidOperationException($"{Name} requires an offset.");
        }

        var offset = CommandArguments.RequireConverted<long>(remainingArguments, 0, "offset");
        var origin = remainingArguments.Count > 1
            ? StreamCommandUtilities.ParseSeekOrigin(remainingArguments[1])
            : SeekOrigin.Begin;

        handle.Seek(offset, origin);
        yield return handle;
    }
}
