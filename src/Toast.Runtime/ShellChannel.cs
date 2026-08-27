using System.Threading.Channels;

namespace Tosh.Runtime;

/// <summary>
/// The outcome of receiving one value from a <see cref="ShellChannel"/>.
/// <see cref="HasValue"/> distinguishes a valid <see langword="null"/>
/// payload from a channel that is closed and drained.
/// </summary>
public readonly record struct ShellChannelReceiveResult(bool HasValue, object? Value);

/// <summary>
/// A typed channel that carries arbitrary shell values between producers and consumers.
/// Wraps <see cref="System.Threading.Channels.Channel{T}"/> with a convenient shell-facing API.
/// </summary>
public sealed class ShellChannel
{
    private readonly Channel<object?> _channel;

    private ShellChannel(Channel<object?> channel)
    {
        _channel = channel;
    }

    /// <summary>Creates an unbounded channel.</summary>
    public static ShellChannel CreateUnbounded()
        => new(Channel.CreateUnbounded<object?>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false }));

    /// <summary>Creates a bounded channel with the given capacity.</summary>
    public static ShellChannel CreateBounded(int capacity)
        => new(Channel.CreateBounded<object?>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        }));

    /// <summary>Whether the write end of the channel is still open.</summary>
    public bool IsOpen => _channel.Reader.Completion.IsCompleted is false;

    /// <summary>
    /// Writes a single value to the channel.
    /// Blocks (asynchronously) when the channel is bounded and full.
    /// Throws <see cref="InvalidOperationException"/> if the channel is already closed.
    /// </summary>
    public ValueTask SendAsync(object? value, CancellationToken cancellationToken = default)
        => _channel.Writer.WriteAsync(value, cancellationToken);

    /// <summary>
    /// Reads the next value from the channel.
    /// A returned <see langword="null"/> is always a payload.
    /// </summary>
    /// <exception cref="ChannelClosedException">
    /// The channel is closed and drained.
    /// </exception>
    public ValueTask<object?> ReceiveAsync(CancellationToken cancellationToken = default)
        => ReadSingleValueAsync(cancellationToken);

    /// <summary>
    /// Reads one value while explicitly distinguishing a valid
    /// <see langword="null"/> payload from closed-and-drained.
    /// </summary>
    public ValueTask<ShellChannelReceiveResult> ReceiveResultAsync(
        CancellationToken cancellationToken = default)
        => ReadSingleResultAsync(cancellationToken);

    /// <summary>
    /// Waits until a receive may succeed without consuming an item.
    /// A <see langword="false"/> result means the channel is closed and drained.
    /// Because channels support multiple readers, callers must still use
    /// <see cref="TryReceive"/> to atomically commit the receive.
    /// </summary>
    internal ValueTask<bool> WaitToReceiveAsync(CancellationToken cancellationToken = default)
        => _channel.Reader.WaitToReadAsync(cancellationToken);

    /// <summary>
    /// Atomically attempts to receive one item. The Boolean result distinguishes
    /// an unavailable item from a valid <see langword="null"/> payload.
    /// </summary>
    internal bool TryReceive(out object? value)
        => _channel.Reader.TryRead(out value);

    private async ValueTask<object?> ReadSingleValueAsync(
        CancellationToken cancellationToken)
    {
        var result = await ReadSingleResultAsync(cancellationToken).ConfigureAwait(false);
        if (!result.HasValue)
        {
            throw new ChannelClosedException();
        }

        return result.Value;
    }

    private async ValueTask<ShellChannelReceiveResult> ReadSingleResultAsync(
        CancellationToken cancellationToken)
    {
        while (await _channel.Reader
                   .WaitToReadAsync(cancellationToken)
                   .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_channel.Reader.TryRead(out var item))
            {
                return new ShellChannelReceiveResult(HasValue: true, item);
            }

            // Readiness is advisory with multiple readers. Another receiver
            // committed the available item, so wait again instead of
            // reporting a fabricated null or completion.
        }

        return new ShellChannelReceiveResult(HasValue: false, Value: null);
    }

    /// <summary>
    /// Returns an <see cref="IAsyncEnumerable{T}"/> that reads all values until the channel closes.
    /// </summary>
    public IAsyncEnumerable<object?> ReadAllAsync(CancellationToken cancellationToken = default)
        => _channel.Reader.ReadAllAsync(cancellationToken);

    /// <summary>
    /// Signals that no further values will be written.
    /// Outstanding reads will drain; subsequent writes throw.
    /// </summary>
    public void Close() => _channel.Writer.TryComplete();

    public override string ToString() => IsOpen ? "ShellChannel(open)" : "ShellChannel(closed)";
}
