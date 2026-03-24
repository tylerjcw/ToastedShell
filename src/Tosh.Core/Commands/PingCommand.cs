using System.Net.NetworkInformation;

namespace Tosh.Core.Commands;

public sealed class PingCommand : ShellCommand
{
    public PingCommand()
        : base("ping", "Pings a host and returns typed reply objects.", "ping [-c count] [-W timeout-ms] <host>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var options = ParseOptions(context.Arguments);

        using var ping = new Ping();

        for (var sequence = 1; sequence <= options.Count; sequence++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var reply = await ping.SendPingAsync(options.Host, options.TimeoutMs);
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

    private static PingCommandOptions ParseOptions(IReadOnlyList<object?> arguments)
    {
        string? host = null;
        var count = 4;
        var timeoutMs = 4_000;

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

            if (text.StartsWith("-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unsupported ping option '{text}'.");
            }

            if (host is not null)
            {
                throw new InvalidOperationException("ping accepts only one host argument.");
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

        return new PingCommandOptions(host, count, timeoutMs);
    }

    private sealed record PingCommandOptions(string Host, int Count, int TimeoutMs);
}
