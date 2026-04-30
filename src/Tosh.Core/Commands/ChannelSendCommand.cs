namespace Tosh.Core.Commands;

[Stdlib(StdlibCategory.Concurrency)]
[CommandCategory("Concurrency")]
[CommandArgument("channel", "The channel to send to.")]
[CommandArgument("values", "One or more values to send. Omit to send pipeline input instead.", Required = false)]
[CommandExample("channel-send $ch hello", Title = "Send a single value to a channel")]
[CommandExample("echo hello world | channel-send $ch", Title = "Forward pipeline items to a channel")]
[CommandNote("Blocks (asynchronously) when the channel is bounded and its buffer is full.")]
[CommandOutput("Emits nothing; sends each value to the channel as a side effect.")]
public sealed class ChannelSendCommand : ShellCommand
{
    public ChannelSendCommand()
        : base("channel-send", "Sends one or more values to a shell channel.", "channel-send <channel> [values ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count < 1)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.channel_send_requires_channel",
                title: "'channel-send' requires a channel argument.",
                label: "pass a ShellChannel value");
        }

        if (context.Arguments[0] is not ShellChannel ch)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.channel_send_requires_channel",
                title: "'channel-send' first argument must be a ShellChannel.",
                argumentIndex: 0,
                label: "this value is not a ShellChannel");
        }

        if (context.Arguments.Count >= 2)
        {
            // Explicit values from arguments.
            for (var i = 1; i < context.Arguments.Count; i++)
            {
                await ch.SendAsync(context.Arguments[i], context.CancellationToken);
            }
        }
        else
        {
            // Drain pipeline input into the channel.
            await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
            {
                await ch.SendAsync(item, context.CancellationToken);
            }
        }

        yield break;
    }
}
