namespace Tosh.Core.Commands;

[Stdlib(StdlibCategory.Pipeline)]
[CommandCategory("Pipeline")]
[CommandArgument("predicate", "A callable or block evaluated for each item. Stops when it returns true.")]
[CommandExample("echo 1 2 3 4 5 | take-until { _ >= 4 }", Title = "Take until a condition is met")]
[CommandExample("1..100 | take-until func(x) => ($x > 10)", Title = "Take until value exceeds 10")]
[CommandOutput("Pipeline items up to (but not including) the first that satisfies the predicate.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Yields items until the predicate returns true.")]
public sealed class TakeUntilCommand : ShellCommand
{
    public TakeUntilCommand()
        : base("take-until", "Yields input values until the predicate becomes true.", "take-until <predicate-expression|callable>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count != 1)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.predicate_expression_required",
                title: "'take-until' requires a predicate expression.",
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

            if (matches)
            {
                yield break;
            }

            yield return item;
        }
    }
}
