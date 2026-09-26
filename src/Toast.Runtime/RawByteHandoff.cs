namespace Tosh.Runtime;

/// <summary>
/// A byte path the engine opens between two pipeline stages, so that an external program's
/// output reaches the next program — or a redirected file — as the bytes it wrote rather than
/// as decoded text lines (<c>TOSH-0012</c>).
/// </summary>
/// <remarks>
/// <para>
/// Every stage reaches the next through the engine's own iterator, so a consumer never sees the
/// enumerable its producer returned, and a marker placed on that enumerable would be lost on the
/// way. The two sides meet here instead. The consumer <see cref="Offer"/>s a destination before
/// it pulls its first item. A producer whose output turned out to be plain bytes
/// <see cref="TryClaim"/>s it, copies everything into it and yields nothing. When nothing is
/// offered — the consumer is a TōSh stage — or the producer speaks TSSP, nothing is claimed and
/// values flow exactly as they always have.
/// </para>
/// <para>
/// Each side is bound to the one <see cref="CommandContext"/> the engine built for its stage.
/// A context copied with <c>with</c> still carries a reference to this object but not the right
/// to use it, so a renderer or a nested callable handed a derived context can neither claim the
/// destination nor offer one.
/// </para>
/// <para>
/// The side that offers a destination keeps owning it. A producer writes and flushes but never
/// closes: the consumer closes its program's stdin once its input is exhausted, and a redirection
/// closes its file when the pipeline ends.
/// </para>
/// </remarks>
public sealed class RawByteHandoff
{
    private readonly object _gate = new();
    private object? _producer;
    private object? _consumer;
    private RawByteDestination? _offered;

    /// <summary>Binds the producing side to the context the engine built for its stage.</summary>
    public void BindProducer(object producer)
    {
        ArgumentNullException.ThrowIfNull(producer);
        lock (_gate)
        {
            _producer = producer;
        }
    }

    /// <summary>Binds the consuming side to the context the engine built for its stage.</summary>
    public void BindConsumer(object consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        lock (_gate)
        {
            _consumer = consumer;
        }
    }

    /// <summary>Whether <paramref name="candidate"/> is the producer this hand-off was built for.</summary>
    public bool IsProducer(object candidate)
    {
        lock (_gate)
        {
            return _producer is not null && ReferenceEquals(_producer, candidate);
        }
    }

    /// <summary>Whether <paramref name="candidate"/> is the consumer this hand-off was built for.</summary>
    public bool IsConsumer(object candidate)
    {
        lock (_gate)
        {
            return _consumer is not null && ReferenceEquals(_consumer, candidate);
        }
    }

    /// <summary>
    /// Makes <paramref name="destination"/> available to the producer until it is claimed or
    /// <see cref="Withdraw"/>n. Call it before pulling the first item, because the producer
    /// decides as soon as it starts.
    /// </summary>
    public void Offer(RawByteDestination destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        lock (_gate)
        {
            _offered = destination;
        }
    }

    /// <summary>
    /// Takes back a destination nobody claimed, so a producer that starts later cannot write to
    /// a stream its owner is about to close.
    /// </summary>
    public void Withdraw()
    {
        lock (_gate)
        {
            _offered = null;
        }
    }

    /// <summary>
    /// Takes the offered destination, if there is one. A claim is exclusive: a producer that
    /// claims must write every byte of its output there and yield nothing.
    /// </summary>
    public bool TryClaim([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out RawByteDestination? destination)
    {
        lock (_gate)
        {
            destination = _offered;
            _offered = null;
            return destination is not null;
        }
    }
}

/// <summary>Where a claimed <see cref="RawByteHandoff"/> sends its bytes.</summary>
/// <param name="Stream">The stream to write to. The producer writes and flushes; it never closes it.</param>
/// <param name="ReaderCanLeave">
/// True when the destination is another program's stdin. A write that fails there means that
/// program has exited, and the producer should stop the way a writer stops on <c>SIGPIPE</c>
/// rather than report an error. False for a file, where a failed write is a real failure.
/// </param>
public sealed record RawByteDestination(Stream Stream, bool ReaderCanLeave)
{
    private const int CopyBufferSize = 64 * 1024;

    /// <summary>
    /// Writes <paramref name="prefix"/> and then everything <paramref name="source"/> yields,
    /// and flushes.
    /// </summary>
    /// <returns>
    /// True when every byte was delivered; false when the reader left first, which only a
    /// destination whose <see cref="ReaderCanLeave"/> is true reports. The caller should then
    /// close <paramref name="source"/>, so the program writing it meets a closed pipe as it
    /// would under any other shell instead of blocking on one nobody drains.
    /// </returns>
    /// <remarks>
    /// A failed read, and any failed write to a file, propagate: they are failures, not a
    /// reader that has had enough.
    /// </remarks>
    public async Task<bool> CopyFromAsync(Stream source, ReadOnlyMemory<byte> prefix, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (!prefix.IsEmpty && !await TryWriteAsync(prefix, cancellationToken))
        {
            return false;
        }

        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(CopyBufferSize);

        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken);

                if (read == 0)
                {
                    break;
                }

                if (!await TryWriteAsync(buffer.AsMemory(0, read), cancellationToken))
                {
                    return false;
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }

        try
        {
            await Stream.FlushAsync(cancellationToken);
        }
        catch (Exception exception) when (ReaderLeft(exception))
        {
            return false;
        }

        return true;
    }

    private async ValueTask<bool> TryWriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        try
        {
            await Stream.WriteAsync(bytes, cancellationToken);
            return true;
        }
        catch (Exception exception) when (ReaderLeft(exception))
        {
            return false;
        }
    }

    // A pipe whose reader exited fails with EPIPE, which .NET reports as an IOException because
    // the runtime ignores SIGPIPE. It is disposed instead when the consuming stage was torn down
    // first — `… | first 1` disposing the program that was reading.
    private bool ReaderLeft(Exception exception)
        => ReaderCanLeave && exception is IOException or ObjectDisposedException;
}
