using Tosh.Runtime;

namespace Tosh.Stdlib.Concurrency;

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
            .Select((channel, index) => WaitForReadinessAsync(index, channel, linkedCts.Token))
            .ToList();

        ChannelSelectResult? selected = null;

        try
        {
            while (pending.Count > 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var readinessTask = await Task.WhenAny(pending);
                pending.Remove(readinessTask);
                var readiness = await readinessTask;

                // A false readiness result means this channel is closed and drained.
                if (!readiness.CanRead)
                {
                    continue;
                }

                context.CancellationToken.ThrowIfCancellationRequested();

                // Readiness is advisory when multiple readers are present. Only this
                // TryReceive commits an item; if another reader won the race, re-arm
                // this channel without disturbing any of the other queues.
                if (readiness.Channel.TryReceive(out var value))
                {
                    selected = new ChannelSelectResult(
                        readiness.Index,
                        readiness.Channel,
                        value);
                    break;
                }

                pending.Add(WaitForReadinessAsync(
                    readiness.Index,
                    readiness.Channel,
                    linkedCts.Token));
            }
        }
        finally
        {
            linkedCts.Cancel();

            if (pending.Count > 0)
            {
                try
                {
                    await Task.WhenAll(pending);
                }
                catch (OperationCanceledException)
                {
                    // Expected when a winner or the caller cancels the remaining
                    // non-destructive readiness waits.
                }
            }
        }

        if (selected is not null)
        {
            yield return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Index"] = selected.Index,
                ["Channel"] = selected.Channel,
                ["Value"] = selected.Value,
            };
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

    private static async Task<ChannelReadiness> WaitForReadinessAsync(
        int index,
        ShellChannel channel,
        CancellationToken cancellationToken)
    {
        var canRead = await channel.WaitToReceiveAsync(cancellationToken);
        return new ChannelReadiness(index, channel, canRead);
    }

    private sealed record ChannelReadiness(int Index, ShellChannel Channel, bool CanRead);

    private sealed record ChannelSelectResult(int Index, ShellChannel Channel, object? Value);
}
