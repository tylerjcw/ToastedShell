namespace Tosh.Core.Commands;

public sealed class IgnoreCommand : ShellCommand
{
    public IgnoreCommand()
        : base("ignore", "Consumes and discards all pipeline input.", "... | ignore") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        await foreach (var _ in context.Input.WithCancellation(context.CancellationToken))
        {
            // Consume and discard
        }

        yield break;
    }
}