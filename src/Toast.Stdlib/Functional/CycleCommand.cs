using Tosh.Runtime;

namespace Tosh.Stdlib.Functional;

[CommandCategory("Functional")]
[CommandExample("echo 1 2 3 | cycle | first 9", Title = "Cycle a sequence")]
[CommandExample("[a b c] | cycle | first 7", Title = "Cycle an array")]
[CommandNote("Produces an infinite sequence. Always pair with `first`, `take-while`, or `take-until` to bound the output.")]
[CommandOutput("The pipeline items repeated in order, infinitely.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Items to repeat cyclically.")]
public sealed class CycleCommand : ShellCommand
{
    public CycleCommand()
        : base("cycle", "Infinitely repeats the pipeline items in order.", "... | cycle") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count > 0)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.cycle_no_arguments",
                title: "'cycle' takes no arguments. Pipe a sequence into it.",
                label: "use '... | cycle'");
        }

        // Materialize the input once so we can loop over it
        var buffer = new List<object?>();
        await foreach (var item in ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken)
                           .WithCancellation(context.CancellationToken))
        {
            buffer.Add(item);
        }

        if (buffer.Count == 0)
        {
            yield break;
        }

        while (true)
        {
            foreach (var item in buffer)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }
        }
    }
}
