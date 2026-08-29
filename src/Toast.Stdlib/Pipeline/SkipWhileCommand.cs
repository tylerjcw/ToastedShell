using Tosh.Runtime;

namespace Tosh.Stdlib.Pipeline;

[CommandCategory("Pipeline")]
[CommandExample("echo 1 2 3 4 | skip-while { _ < 3 }")]
[CommandExample("echo 1 2 3 4 | skip-while func(x) => ($x < 3)")]
[CommandOutput("All input items starting from (and including) the first one for which the predicate result was falsy.")]
[CommandStreaming(StreamingBehavior.Lazy)]
public sealed class SkipWhileCommand : ShellCommand
{
    public SkipWhileCommand()
        : base("skip-while", "Skips input values while the predicate remains true.", "skip-while <predicate-expression|callable>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count != 1)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.predicate_expression_required",
                title: "'skip-while' requires a predicate expression.",
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
                skipping = await FunctionalCommandUtilities.EvaluatePredicateAsync(
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
