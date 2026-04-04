namespace Tosh.Core.Commands;

public sealed class GroupWhileCommand : ShellCommand
{
    public GroupWhileCommand()
        : base("group-while", "Groups consecutive items while the predicate holds.", "group-while <callable|block>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count != 1)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::group_while_requires_callable_or_block",
                title: "'group-while' requires exactly one callable value or block.",
                label: "pass a predicate block like '{ _ != \"\" }'");
        }

        var predicate = FunctionalCommandUtilities.RequireCallableOrBlock(context, 0);
        var currentGroup = new List<object?>();

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
                currentGroup.Add(item);
            }
            else
            {
                if (currentGroup.Count > 0)
                {
                    yield return currentGroup.ToArray();
                    currentGroup.Clear();
                }

                currentGroup.Add(item);
            }
        }

        if (currentGroup.Count > 0)
        {
            yield return currentGroup.ToArray();
        }
    }
}
