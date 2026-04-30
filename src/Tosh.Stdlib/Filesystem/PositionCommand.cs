using Tosh.Runtime;

namespace Tosh.Stdlib.Filesystem;

[CommandCategory("Filesystem")]
[CommandArgument("handle ...", "One or more managed file handles whose current position should be reported.", Required = false)]
[CommandExample("position $handle")]
[CommandExample("echo $handle | position")]
[CommandOutput("Returns the current byte position for each supplied handle when that position can be reported safely.")]
[PipelineInput(AcceptsRecord = true, Description = "Consumes piped file handles when explicit handles are omitted.")]
[CommandNote("These commands work with managed file handles returned by `open-file` or by `FileSystemEntry` methods like `OpenText()` and `OpenRead()`. `seek` returns the handle so you can keep piping through the stream workflow, while `copy-to` copies from one compatible handle into another.")]
public sealed class PositionCommand : ShellCommand
{
    public PositionCommand()
        : base("position", "Returns the current stream position for one or more managed file handles.", "position [handle ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var handles = await StreamCommandUtilities.ResolveHandleListAsync(context);

        foreach (var handle in handles)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (handle.Position is not long position)
            {
                throw new InvalidOperationException("Position is not available for this handle. Text reader handles do not expose a stable current byte position because .NET buffers decoded text.");
            }

            yield return position;
        }
    }
}
