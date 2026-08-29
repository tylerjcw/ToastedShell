using Tosh.Runtime;

namespace Tosh.Stdlib.Pipeline;

[CommandCategory("Pipeline")]
[CommandArgument("size", "The number of items in each sliding window.")]
[CommandArgument("callable|block", "Optional transform applied to each window.", Required = false)]
[CommandExample("echo 1 2 3 4 5 | window 3", Title = "Sliding windows of size 3")]
[CommandExample("1..10 | window 2 func(a, b) => ($a + $b)", Title = "Pairwise sums")]
[CommandOutput("Arrays (or transformed results) for each sliding window position.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Yields overlapping windows of the specified size as the pipeline flows.")]
[CommandStreaming(StreamingBehavior.Lazy)]
public sealed class WindowCommand : ShellCommand
{
    public WindowCommand()
        : base("window", "Yields sliding windows of a given size over the pipeline.", "window <size> [callable|block]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count < 1 || context.Arguments.Count > 2)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.window_requires_size",
                title: "'window' requires a size and an optional callable or block.",
                label: "use 'window <size> [block]'");
        }

        if (!TypeConversion.TryConvert(context.Arguments[0], typeof(int), out var converted) || converted is not int size || size <= 0)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.window_requires_positive_integer",
                title: "'window' requires a positive integer size.",
                argumentIndex: 0,
                label: "expected a positive integer");
        }

        object? operation = context.Arguments.Count == 2
            ? FunctionalCommandUtilities.RequireCallableOrBlock(context, 1)
            : null;

        var buffer = new Queue<object?>(size);

        await foreach (var item in ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken)
                           .WithCancellation(context.CancellationToken))
        {
            buffer.Enqueue(item);

            if (buffer.Count > size)
            {
                buffer.Dequeue();
            }

            if (buffer.Count == size)
            {
                var windowArray = buffer.ToArray();

                if (operation is not null)
                {
                    var result = await FunctionalCommandUtilities.RequireSingleResultAsync(
                        context,
                        operation,
                        [windowArray],
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["_"] = windowArray,
                        });

                    yield return result;
                }
                else
                {
                    yield return windowArray;
                }
            }
        }
    }
}
