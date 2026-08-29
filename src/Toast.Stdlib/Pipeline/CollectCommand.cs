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

        // `TS-P2-74`. A single collection arriving as one item means its elements, which
        // is what every neighbouring stage already assumed. Measured on a bare
        // `[1, 2, 3]`: `count` reported 3, `each` 3, `where` 2, `skip 1` 2, `first` the
        // first element — and `collect` alone reported 1, because a pipeline head yields
        // one value and `collect` was the only stage that did not enumerate it.
        //
        // The visible symptom was an asymmetry with variables:
        // `"a.b.c".Split(".") | collect` gave a one-element list holding the array while
        // `$v | collect` on the same array gave three, because a variable binding replays
        // as a pipeline and an expression does not. That silently made the namespace of
        // every diagnostic code in a generated file come out `?`.
        //
        // Fixed here rather than at the head: spreading every list-valued head was tried
        // and breaks `[] | to json`, which must serialize the empty array instead of
        // sending nothing on. Collections are a *stage* question, and this stage belongs
        // with `count` and `each` rather than with `to json`.
        if (items.Count == 1 &&
            items[0] is System.Collections.IEnumerable single &&
            items[0] is not string &&
            (items[0] is System.Collections.IList or Array))
        {
            yield return single.Cast<object?>().ToArray();
            yield break;
        }

        yield return items.ToArray();
    }
}
