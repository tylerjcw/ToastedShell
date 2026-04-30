using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

using Tosh.Runtime;

namespace Tosh.Stdlib.Net;

[Stdlib(StdlibCategory.Net)]
[CommandCategory("Network")]
[CommandArgument("host", "Host name or IP address to ping.")]
[CommandOption("-c, --count <count>", "Number of echo requests to send. Defaults to 4.")]
[CommandOption("-W, --timeout <milliseconds>", "Per-request timeout in milliseconds. Defaults to 4000.")]
[CommandOption("-s, --size <bytes>", "Payload size in bytes. Defaults to 32.")]
[CommandOption("-i, --interval <seconds>", "Delay between requests, in seconds. Defaults to 1.")]
[CommandOption("-t, --ttl <hops>", "Set the IP time-to-live value for requests.")]
[CommandOption("-4", "Resolve and use an IPv4 address.")]
[CommandOption("-6", "Resolve and use an IPv6 address.")]
[CommandOption("-D, --dont-fragment", "Set the don't-fragment flag where supported.")]
[CommandExample("ping -c 3 localhost", Title = "Ping localhost three times")]
[CommandExample("ping -4 -W 1000 example.com | get { Host, Sequence, Status, RoundtripTime }", Title = "Project typed ping replies")]
[CommandSideEffects(Network = true)]
[CommandOutput("Per-reply records: address, round-trip time, and status flag.")]
public sealed class PingCommand : ShellCommand
{
    public PingCommand()
        : base("ping", "Pings a host and returns typed reply objects.", "ping [-c count] [-W timeout-ms] [-s size] [-i interval] [-t ttl] [-4|-6] [-D] <host>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var options = ParseOptions(context.Arguments);

        using var ping = new Ping();

        var needsRawSocket = options.PayloadSize != 32 || options.Ttl is not null || options.DontFragment;
        byte[]? buffer = needsRawSocket ? new byte[options.PayloadSize] : null;
        PingOptions? pingOptions = null;
        if (needsRawSocket)
        {
            pingOptions = new PingOptions();
            if (options.Ttl is { } ttl) pingOptions.Ttl = ttl;
            if (options.DontFragment) pingOptions.DontFragment = true;
        }

        var targetAddress = await ResolveHostAsync(options.Host, options.AddressFamily);

        for (var sequence = 1; sequence <= options.Count; sequence++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (sequence > 1 && options.IntervalMs > 0)
            {
                await Task.Delay(options.IntervalMs, context.CancellationToken);
            }

            var reply = needsRawSocket
                ? await ping.SendPingAsync(targetAddress, options.TimeoutMs, buffer!, pingOptions!)
                : await ping.SendPingAsync(targetAddress, options.TimeoutMs);
            yield return new PingReplyInfo(
                options.Host,
                reply.Address,
                sequence,
                reply.Status,
                reply.Status == IPStatus.Success ? TimeSpan.FromMilliseconds(reply.RoundtripTime) : null,
                reply.Buffer?.Length ?? 0,
                reply.Options?.Ttl,
                reply.Options?.DontFragment);
        }
    }

    private static async Task<IPAddress> ResolveHostAsync(string host, AddressFamily? preferredFamily)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return literal;
        }

        var entry = await Dns.GetHostEntryAsync(host);

        if (preferredFamily is { } family)
        {
            var match = entry.AddressList.FirstOrDefault(a => a.AddressFamily == family);
            if (match is not null) return match;
            throw new InvalidOperationException($"No {(family == AddressFamily.InterNetwork ? "IPv4" : "IPv6")} address found for '{host}'.");
        }

        return entry.AddressList.Length > 0
            ? entry.AddressList[0]
            : throw new InvalidOperationException($"Could not resolve host '{host}'.");
    }

    private static PingCommandOptions ParseOptions(IReadOnlyList<object?> arguments)
    {
        string? host = null;
        var count = 4;
        var timeoutMs = 4_000;
        var payloadSize = 32;
        var intervalMs = 1_000;
        int? ttl = null;
        AddressFamily? addressFamily = null;
        var dontFragment = false;

        for (var index = 0; index < arguments.Count; index++)
        {
            var text = arguments[index]?.ToString();

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (text is "-c" or "--count")
            {
                count = CommandArguments.RequireConverted<int>(arguments, ++index, "count");
                continue;
            }

            if (text is "-W" or "--timeout")
            {
                timeoutMs = CommandArguments.RequireConverted<int>(arguments, ++index, "timeout");
                continue;
            }

            if (text is "-s" or "--size")
            {
                payloadSize = CommandArguments.RequireConverted<int>(arguments, ++index, "size");
                continue;
            }

            if (text is "-i" or "--interval")
            {
                var seconds = CommandArguments.RequireConverted<double>(arguments, ++index, "interval");
                intervalMs = (int)(seconds * 1000);
                continue;
            }

            if (text is "-t" or "--ttl")
            {
                ttl = CommandArguments.RequireConverted<int>(arguments, ++index, "ttl");
                continue;
            }

            if (text is "-4")
            {
                addressFamily = AddressFamily.InterNetwork;
                continue;
            }

            if (text is "-6")
            {
                addressFamily = AddressFamily.InterNetworkV6;
                continue;
            }

            if (text is "-D" or "--dont-fragment")
            {
                dontFragment = true;
                continue;
            }

            if (text.StartsWith("-", StringComparison.Ordinal))
            {
                throw new CommandArgumentException(index, $"Unsupported ping option '{text}'.");
            }

            if (host is not null)
            {
                throw new CommandArgumentException(index, "ping accepts only one host argument.");
            }

            host = text;
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            throw new InvalidOperationException("Missing required argument: host.");
        }

        if (count <= 0)
        {
            throw new InvalidOperationException("ping count must be greater than zero.");
        }

        if (timeoutMs <= 0)
        {
            throw new InvalidOperationException("ping timeout must be greater than zero.");
        }

        if (payloadSize < 0 || payloadSize > 65_500)
        {
            throw new InvalidOperationException("ping payload size must be between 0 and 65500.");
        }

        if (ttl is <= 0 or > 255)
        {
            throw new InvalidOperationException("ping TTL must be between 1 and 255.");
        }

        return new PingCommandOptions(host, count, timeoutMs, payloadSize, intervalMs, ttl, addressFamily, dontFragment);
    }

    private sealed record PingCommandOptions(string Host, int Count, int TimeoutMs, int PayloadSize, int IntervalMs, int? Ttl, AddressFamily? AddressFamily, bool DontFragment);
}
