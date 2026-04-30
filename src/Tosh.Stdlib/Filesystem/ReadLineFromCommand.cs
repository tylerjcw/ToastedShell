using Tosh.Runtime;

namespace Tosh.Stdlib.Filesystem;

[CommandCategory("Filesystem")]
[CommandArgument("handle", "The managed text file handle to read from.", Required = false)]
[CommandExample("$handle | read-line-from")]
[CommandExample("read-line-from $handle")]
[CommandOutput("Returns the next line as a ShellTextLine value, or nothing at end-of-file.")]
[PipelineInput(AcceptsRecord = true, Description = "Consumes a piped file handle when no explicit handle argument is supplied.")]
[CommandNote("These commands work with managed file handles returned by `open-file` or by `FileSystemEntry` methods like `OpenText()` and `OpenRead()`. `seek` returns the handle so you can keep piping through the stream workflow, while `copy-to` copies from one compatible handle into another.")]
public sealed class ReadLineFromCommand : ShellCommand
{
    public ReadLineFromCommand(string name = "read-line-from")
        : base(name, "Reads the next text line from an open managed file handle.", $"{name} [handle]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var (handle, _) = await StreamCommandUtilities.ResolveSingleReadableHandleAsync(context);
        var line = handle.ReadLine();

        if (line is not null)
        {
            yield return new ShellTextLine(line);
        }
    }
}
