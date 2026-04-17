namespace Tosh.Core.Commands;

[CommandCategory("Pipeline")]
[CommandExample("echo 1 2 3 | reverse", Title = "Reverse a sequence")]
[CommandExample("ls | sort .Name | reverse", Title = "Reverse a sorted listing")]
[CommandOutput("All pipeline objects in reversed order.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Buffers the pipeline then yields items in reverse order.")]
public sealed class ReverseCommand : ShellCommand
{
    public ReverseCommand()
        : base("reverse", "Reverses the order of the current pipeline objects.", "reverse") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count != 0)
        {
            throw new InvalidOperationException("The 'reverse' command does not accept arguments.");
        }

        var items = new List<object?>();

        await foreach (var item in ShellIterationUtilities.ReplaySingleInputCollectionAsync(context.Input, context.CancellationToken)
                           .WithCancellation(context.CancellationToken))
        {
            items.Add(item);
        }

        for (var index = items.Count - 1; index >= 0; index--)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            yield return items[index];
        }
    }
}
