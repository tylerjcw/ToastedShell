namespace Tosh.Core.Commands;

[CommandCategory("Concurrency")]
[CommandArgument("channel", "The channel to receive from. Omit to read from pipeline input.")]
[CommandExample("channel-recv $ch | each { |v| echo $v }", Title = "Stream all values from a channel")]
[CommandOutput("Streams every value from the channel until it is closed.")]
[CommandNote("Blocks until a value is available. Returns when the channel is closed and drained.")]
public sealed class ChannelRecvCommand : ShellCommand
{
    public ChannelRecvCommand()
        : base("channel-recv", "Receives all values from a shell channel.", "channel-recv <channel>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        ShellChannel? ch = null;

        if (context.Arguments.Count >= 1)
        {
            if (context.Arguments[0] is not ShellChannel channelArg)
            {
                throw context.CreateDiagnostic(
                    code: "tosh::runtime::channel_recv_requires_channel",
                    title: "'channel-recv' first argument must be a ShellChannel.",
                    argumentIndex: 0,
                    label: "this value is not a ShellChannel");
            }

            ch = channelArg;
        }
        else
        {
            // Accept from pipeline (first item must be the channel).
            await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
            {
                if (item is ShellChannel piped)
                {
                    ch = piped;
                    break;
                }
            }
        }

        if (ch is null)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::channel_recv_requires_channel",
                title: "'channel-recv' requires a ShellChannel argument or piped input.",
                label: "pass a ShellChannel value");
        }

        await foreach (var value in ch.ReadAllAsync(context.CancellationToken))
        {
            yield return value;
        }
    }
}
