namespace Tosh.Core.Commands.Pipeline;

[Stdlib(StdlibCategory.Pipeline)]
[CommandCategory("Pipeline")]
[CommandArgument("separator", "The value to insert between each pipeline item.")]
[CommandExample("echo 1 2 3 | intersperse 0", Title = "Insert zeros between items")]
[CommandExample("echo a b c | intersperse \"-\"", Title = "Insert dashes")]
[CommandOutput("The original items with the separator inserted between each pair.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Items are yielded with the separator between each pair.")]
public sealed class IntersperseCommand : ShellCommand
{
    public IntersperseCommand()
        : base("intersperse", "Inserts a separator value between each pair of pipeline items.", "... | intersperse <separator>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count != 1)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.intersperse_requires_separator",
                title: "'intersperse' requires exactly one separator argument.",
                label: "use '... | intersperse <separator>'");
        }

        var separator = context.Arguments[0];
        var first = true;

        await foreach (var item in ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken)
                           .WithCancellation(context.CancellationToken))
        {
            if (!first)
            {
                yield return separator;
            }

            yield return item;
            first = false;
        }
    }
}
