namespace Tosh.Core.Commands.Pipeline;

[Stdlib(StdlibCategory.Pipeline)]
[CommandCategory("Pipeline")]
[CommandArgument("callable|block", "A predicate that receives each item. A new group starts when it returns false.")]
[CommandExample("echo 1 1 2 2 3 | group-while { _ == $prev }", Title = "Group equal consecutive values")]
[CommandExample("echo 1 2 3 10 11 12 | group-while func(x, prev) => ($x - $prev <= 1)", Title = "Group runs of nearly consecutive numbers")]
[CommandOutput("Arrays of consecutive items grouped while the predicate holds.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Groups consecutive pipeline items while the predicate returns true.")]
public sealed class GroupWhileCommand : ShellCommand
{
    public GroupWhileCommand()
        : base("group-while", "Groups consecutive items while the predicate holds.", "group-while <callable|block>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count != 1)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.group_while_requires_callable_or_block",
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
