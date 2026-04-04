namespace Tosh.Core.Commands;

public sealed class SkipUntilCommand : ShellCommand
{
    public SkipUntilCommand()
        : base("skip-until", "Skips input values until the predicate becomes true.", "skip-until <predicate-expression|callable>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count != 1)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::predicate_expression_required",
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
