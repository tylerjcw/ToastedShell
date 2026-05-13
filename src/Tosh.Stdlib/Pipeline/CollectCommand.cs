using Tosh.Runtime;

namespace Tosh.Stdlib.Pipeline;

[CommandCategory("Pipeline")]
[CommandExample("echo 1 2 3 | collect", Title = "Collect scalar pipeline items into one array")]
[CommandExample("ls *.cs | collect", Title = "Capture a multi-item file listing as one value")]
[CommandExample("findmnt -l | where _.FsType == ext4 | collect", Title = "Buffer filtered structured rows into one array")]
[CommandOutput("Returns a single array containing the pipeline items in order.", ClrType = typeof(IAsyncEnumerable<object[]>))]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, AcceptsList = true, AcceptsTable = true, Description = "Consumes the current pipeline and buffers every incoming item into one array result.")]
[CommandStreaming(StreamingBehavior.Eager)]
public sealed class CollectCommand : ShellCommand
{
    public CollectCommand()
        : base("collect", "Collects all pipeline items into a single list.", "collect") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var items = await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);
        yield return items.ToArray();
    }
}
