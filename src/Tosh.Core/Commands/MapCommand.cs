namespace Tosh.Core.Commands;

[Stdlib(StdlibCategory.Pipeline)]
[CommandCategory("Pipeline")]
[CommandArgument("callable|block", "A lambda or block that transforms each input item into exactly one output value.")]
[CommandExample("echo 1 2 3 | map func(x) => ($x * 2)", Title = "Transform values with a lambda")]
[CommandExample("ls | map { _.Name }", Title = "Transform values with a block")]
[CommandOutput("Returns one transformed value for each input item.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Consumes the current pipeline and emits one transformed value per input item.")]
public sealed class MapCommand : ShellCommand
{
    public MapCommand()
        : base("map", "Transforms each pipeline value with a callable value or block.", "map <callable|block>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count != 1)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.map_requires_callable_or_block",
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
