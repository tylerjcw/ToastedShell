namespace Tosh.Core.Commands;

public sealed class FindIndexCommand : ShellCommand
{
    public FindIndexCommand()
        : base("find-index", "Returns the 0-based index of the first pipeline value matching the predicate, or -1 if none match.", "find-index <callable|block>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count != 1)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::find_index_requires_callable_or_block",
                title: "'find-index' requires exactly one callable value or block.",
                label: "pass a lambda like 'func(x) => ...' or a block like '{ ... }'");
        }

        var operation = FunctionalCommandUtilities.RequireCallableOrBlock(context, 0);
        var index = 0;

        await foreach (var item in ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken)
                           .WithCancellation(context.CancellationToken))
        {
            var matches = await FunctionalCommandUtilities.EvaluatePredicateAsync(
                context,
                operation,
                [item],
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["_"] = item,
                });

            if (matches)
            {
                yield return index;
                yield break;
            }

            index++;
        }

        yield return -1;
    }
}
