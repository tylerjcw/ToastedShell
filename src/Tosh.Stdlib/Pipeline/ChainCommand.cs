using Tosh.Runtime;

namespace Tosh.Stdlib.Pipeline;

[Stdlib(StdlibCategory.Pipeline)]
[CommandCategory("Pipeline")]
[CommandArgument("sequences", "One or more arrays, lists, or ranges to concatenate after the pipeline.")]
[CommandExample("echo 1 2 | chain [3 4] [5 6]", Title = "Concatenate multiple sequences")]
[CommandExample("echo a b c | chain [d e f]", Title = "Append to pipeline")]
[CommandExample("chain [1 2] [3 4] [5 6]", Title = "Concatenate without pipeline input")]
[CommandOutput("All items from the pipeline (if any) followed by all items from each argument sequence.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Pipeline items are yielded first, followed by each argument sequence.")]
public sealed class ChainCommand : ShellCommand
{
    public ChainCommand()
        : base("chain", "Lazily concatenates the pipeline with one or more sequences.", "... | chain <sequence> [sequence ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count < 1)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.chain_requires_sequence",
                title: "'chain' requires at least one sequence argument.",
                label: "use '... | chain <array> [array ...]'");
        }

        // Yield pipeline items first
        await foreach (var item in ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken)
                           .WithCancellation(context.CancellationToken))
        {
            yield return item;
        }

        // Then yield items from each argument sequence
        foreach (var arg in context.Arguments)
        {
            foreach (var item in ShellIterationUtilities.ExpandIterationItems(arg))
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }
        }
    }
}
