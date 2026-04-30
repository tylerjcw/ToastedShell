using Tosh.Runtime;

namespace Tosh.Stdlib.Pipeline;

[CommandCategory("Pipeline")]
[CommandArgument("predicate", "A callable or block evaluated for each item. Starts yielding when it returns true.")]
[CommandExample("echo 1 2 3 4 5 | skip-until { _ >= 3 }", Title = "Skip until a condition is met")]
[CommandOutput("Pipeline items starting from the first that satisfies the predicate.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Discards items until the predicate returns true, then yields the rest.")]
public sealed class SkipUntilCommand : ShellCommand
{
    public SkipUntilCommand()
        : base("skip-until", "Skips input values until the predicate becomes true.", "skip-until <predicate-expression|callable>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count != 1)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.predicate_expression_required",
                title: "'skip-until' requires a predicate expression.",
                label: "write a predicate block like '{ ... }' or pass a callable value",
                help: "predicate commands now use one expression mode everywhere.");
        }

        var predicate = await FunctionalCommandUtilities.ResolveCallableOrBlockAsync(
            context,
            FunctionalCommandUtilities.RequireCallableOrBlock(context, 0));
        var skipping = true;

        await foreach (var item in ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken)
                           .WithCancellation(context.CancellationToken))
        {
            if (skipping)
            {
                skipping = !await FunctionalCommandUtilities.EvaluatePredicateAsync(
                    context,
                    predicate,
                    [item],
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["_"] = item,
                    });

                if (skipping)
                {
                    continue;
                }
            }

            yield return item;
        }
    }
}
