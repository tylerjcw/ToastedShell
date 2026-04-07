namespace Tosh.Core.Commands;

[CommandCategory("Pipeline")]
public sealed class MapCommand : ShellCommand
{
    public MapCommand()
        : base("map", "Transforms each pipeline value with a callable value or block.", "map <callable|block>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count != 1)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::map_requires_callable_or_block",
                title: "'map' requires exactly one callable value or block.",
                label: "pass a lambda like 'func(x) => ...' or a block like '{ ... }'");
        }

        var operation = FunctionalCommandUtilities.RequireCallableOrBlock(context, 0);

        await foreach (var item in ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken)
                           .WithCancellation(context.CancellationToken))
        {
            yield return await FunctionalCommandUtilities.RequireSingleResultAsync(
                context,
                operation,
                [item],
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["_"] = item,
                });
        }
    }
}
