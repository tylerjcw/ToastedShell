using System.Threading.Channels;

namespace Tosh.Core;

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
    /// Returns <c>null</c> when the channel is closed and drained.
    /// </summary>
    public ValueTask<object?> ReceiveAsync(CancellationToken cancellationToken = default)
        => ReadSingleAsync(cancellationToken);

    private async ValueTask<object?> ReadSingleAsync(CancellationToken cancellationToken)
    {
        await _channel.Reader.WaitToReadAsync(cancellationToken);
        return _channel.Reader.TryRead(out var item) ? item : null;
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
