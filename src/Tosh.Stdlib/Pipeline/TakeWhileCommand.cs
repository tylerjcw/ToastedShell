using Tosh.Runtime;

namespace Tosh.Stdlib.Pipeline;

[Stdlib(StdlibCategory.Pipeline)]
[CommandCategory("Pipeline")]
[CommandExample("echo 1 2 3 4 | take-while { _ < 3 }")]
[CommandExample("echo 1 2 3 4 | take-while func(x) => ($x < 3)")]
[CommandOutput("Input items from the front of the stream up to (but not including) the first one for which the predicate returned false.")]
public sealed class TakeWhileCommand : ShellCommand
{
    public TakeWhileCommand()
        : base("take-while", "Yields input values while the predicate remains true.", "take-while <predicate-expression|callable>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count != 1)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.predicate_expression_required",
                title: "'take-while' requires a predicate expression.",
                label: "write a predicate block like '{ ... }' or pass a callable value",
                help: "predicate commands now use one expression mode everywhere.");
        }

        var predicate = await FunctionalCommandUtilities.ResolveCallableOrBlockAsync(
            context,
            FunctionalCommandUtilities.RequireCallableOrBlock(context, 0));

        await foreach (var item in ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken)
                           .WithCancellation(context.CancellationToken))
        {
            var matches = await FunctionalCommandUtilities.EvaluatePredicateAsync(
                context,
                predicate,
                [item],
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["_"] = item,
                });

            if (!matches)
            {
                yield break;
            }

            yield return item;
        }
    }
}
