namespace Tosh.Core.Commands.Concurrency;

[Stdlib(StdlibCategory.Concurrency)]
[CommandCategory("Concurrency")]
[CommandArgument("channel", "The channel to close. Omit to close channel(s) from pipeline input.")]
[CommandExample("channel-close $ch", Title = "Signal that no more values will be sent")]
[CommandNote("After closing, channel-recv will drain any buffered values and then complete. Closing an already-closed channel is a no-op.")]
[CommandOutput("Emits nothing; closes the channel(s) as a side effect.")]
public sealed class ChannelCloseCommand : ShellCommand
{
    public ChannelCloseCommand()
        : base("channel-close", "Closes a shell channel, signalling no further values will be sent.", "channel-close <channel>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count >= 1)
        {
            foreach (var arg in context.Arguments)
            {
                if (arg is ShellChannel ch)
                {
                    ch.Close();
                }
            }
        }
        else
        {
            await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
            {
                if (item is ShellChannel ch)
                {
                    ch.Close();
                }
            }
        }

        // channel-close produces no output.
        yield break;
    }
}
