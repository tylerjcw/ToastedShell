namespace Tosh.Core.Commands;

[CommandCategory("Concurrency")]
[CommandArgument("capacity", "Optional maximum number of items the channel can buffer. Omit for unbounded.", Required = false)]
[CommandExample("var ch = channel", Title = "Create an unbounded channel")]
[CommandExample("var ch = channel 10", Title = "Create a bounded channel that buffers up to 10 items")]
[CommandOutput("Returns a ShellChannel value.")]
[CommandNote("Send values with channel-send, receive with channel-recv, and close with channel-close.")]
public sealed class ChannelCommand : ShellCommand
{
    public ChannelCommand()
        : base("channel", "Creates a new shell channel for sending and receiving values.", "channel [capacity]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        yield return Create(context);
        await Task.CompletedTask;
    }

    private static ShellChannel Create(CommandContext context)
    {
        if (context.Arguments.Count == 0)
        {
            return ShellChannel.CreateUnbounded();
        }

        var raw = context.Arguments[0];
        var capacity = raw switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => -1,
        };

        if (capacity <= 0)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::channel_invalid_capacity",
                title: "'channel' capacity must be a positive integer.",
                argumentIndex: 0,
                label: "provide a positive integer");
        }

        return ShellChannel.CreateBounded(capacity);
    }
}
