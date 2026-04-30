namespace Tosh.Core.Commands;

[Stdlib(StdlibCategory.Concurrency)]
[CommandCategory("Concurrency")]
[CommandArgument("channels", "One or more channels to wait on. If omitted, reads channels from pipeline input.", Required = false)]
[CommandExample("channel-select $ch1 $ch2", Title = "Return the first channel with a value")]
[CommandOutput("Returns a record with Index, Channel, and Value for the first available receive.")]
public sealed class ChannelSelectCommand : ShellCommand
{
    public ChannelSelectCommand()
        : base("channel-select", "Waits for the first available value from multiple channels.", "channel-select [channel ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var channels = await ResolveChannelsAsync(context);
        if (channels.Count == 0)
        {
            yield break;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        var pending = channels
            .Select((channel, index) => ReceiveResultAsync(index, channel, linkedCts.Token))
            .ToList();

        while (pending.Count > 0)
        {
            var winnerTask = await Task.WhenAny(pending);
            pending.Remove(winnerTask);
            var winner = await winnerTask;

            // Closed/drained channel without a value; continue waiting on the rest.
            if (!winner.HasValue)
            {
                continue;
            }

            linkedCts.Cancel();
            yield return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Index"] = winner.Index,
                ["Channel"] = winner.Channel,
                ["Value"] = winner.Value,
            };
            yield break;
        }
    }

    private static async Task<IReadOnlyList<ShellChannel>> ResolveChannelsAsync(CommandContext context)
    {
        var channels = new List<ShellChannel>();

        if (context.Arguments.Count > 0)
        {
            for (var i = 0; i < context.Arguments.Count; i++)
            {
                if (context.Arguments[i] is not ShellChannel ch)
                {
                    throw context.CreateDiagnostic(
                        code: "tosh.runtime.channel_select_requires_channel",
                        title: "'channel-select' arguments must be ShellChannel values.",
                        argumentIndex: i,
                        label: "this value is not a ShellChannel");
                }

                channels.Add(ch);
            }

            return channels;
        }

        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            if (item is not ShellChannel ch)
            {
                throw context.CreateDiagnostic(
                    code: "tosh.runtime.channel_select_requires_channel",
                    title: "'channel-select' expects ShellChannel values from pipeline input.",
                    label: "piped value is not a ShellChannel");
            }

            channels.Add(ch);
        }

        return channels;
    }

    private static async Task<ChannelSelectResult> ReceiveResultAsync(int index, ShellChannel channel, CancellationToken cancellationToken)
    {
        try
        {
            var value = await channel.ReceiveAsync(cancellationToken);
            return value is null
                ? new ChannelSelectResult(index, channel, null, HasValue: false)
                : new ChannelSelectResult(index, channel, value, HasValue: true);
        }
        catch (OperationCanceledException)
        {
            return new ChannelSelectResult(index, channel, null, HasValue: false);
        }
    }

    private sealed record ChannelSelectResult(int Index, ShellChannel Channel, object? Value, bool HasValue);
}
