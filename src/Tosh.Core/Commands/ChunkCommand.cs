namespace Tosh.Core.Commands;

[Stdlib(StdlibCategory.Pipeline)]
[CommandCategory("Pipeline")]
[CommandArgument("size", "The number of items per chunk.")]
[CommandExample("echo 1 2 3 4 5 | chunk 2", Title = "Group into pairs")]
[CommandExample("1..10 | chunk 3 | map { count }", Title = "Chunk then count each group")]
[CommandOutput("Arrays of up to `size` items. The last chunk may be smaller.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Collects pipeline items into fixed-size batches.")]
public sealed class ChunkCommand : ShellCommand
{
    public ChunkCommand()
        : base("chunk", "Groups pipeline items into fixed-size batches.", "chunk <size>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count != 1)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.chunk_requires_size",
                title: "'chunk' requires exactly one integer size argument.",
                label: "use 'chunk <size>'");
        }

        if (!TypeConversion.TryConvert(context.Arguments[0], typeof(int), out var converted) || converted is not int size || size <= 0)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.chunk_requires_positive_integer",
                title: "'chunk' requires a positive integer size.",
                argumentIndex: 0,
                label: "expected a positive integer");
        }

        var buffer = new List<object?>(size);

        await foreach (var item in ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken)
                           .WithCancellation(context.CancellationToken))
        {
            buffer.Add(item);

            if (buffer.Count == size)
            {
                yield return buffer.ToArray();
                buffer.Clear();
            }
        }

        if (buffer.Count > 0)
        {
            yield return buffer.ToArray();
        }
    }
}
