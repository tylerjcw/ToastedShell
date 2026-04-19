namespace Tosh.Core.Commands;

[CommandCategory("Pipeline")]
[CommandArgument("start", "Optional starting index (default: 0).", Required = false)]
[CommandExample("echo a b c | enumerate", Title = "Default 0-based indexing")]
[CommandExample("echo a b c | enumerate 1", Title = "1-based indexing")]
[CommandOutput("Two-element arrays [index, item] for each pipeline item.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Each item is paired with its index.")]
public sealed class EnumerateCommand : ShellCommand
{
    public EnumerateCommand()
        : base("enumerate", "Pairs each pipeline item with its index, yielding [index, item] arrays.", "... | enumerate [start]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count > 1)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::enumerate_too_many_args",
                title: "'enumerate' accepts at most one argument (the starting index).",
                label: "use '... | enumerate [start]'");
        }

        long index = 0;
        if (context.Arguments.Count == 1)
        {
            index = Convert.ToInt64(context.Arguments[0]);
        }

        await foreach (var item in ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken)
                           .WithCancellation(context.CancellationToken))
        {
            yield return new object?[] { index, item };
            index++;
        }
    }
}
