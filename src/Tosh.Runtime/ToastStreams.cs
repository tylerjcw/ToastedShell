using System.Text;

namespace Tosh.Runtime;

/// <summary>
/// The destinations every host has, and the adapter that makes a
/// <see cref="TextWriter"/> one of them.
/// </summary>
/// <remarks>
/// `TOAST-0015`. The adapter is the load-bearing piece: it is what lets a shell session's
/// writer be *a* destination rather than *the* destination, which is the whole difference
/// between redirecting to a file and mutating the session to point at one.
/// </remarks>
public static class ToastStreams
{
    /// <summary>A destination that discards everything written to it.</summary>
    /// <remarks>
    /// The default for a host that has not supplied one, so a `ToastRuntime` standing alone
    /// can run a program that writes without a shell, a terminal, or a null check at every
    /// call site.
    /// </remarks>
    public static IToastStream Null { get; } = new NullStream();

    /// <summary>Wraps <paramref name="writer"/> as a Tōast destination.</summary>
    public static IToastStream FromWriter(TextWriter writer) => new TextWriterStream(writer);

    /// <summary>
    /// One destination that writes to several — what <c>cmd out&gt; a out&gt; b</c> needs.
    /// </summary>
    public static IToastStream Composite(IReadOnlyList<IToastStream> destinations)
        => destinations.Count == 1 ? destinations[0] : new CompositeStream(destinations);

    private sealed class NullStream : IToastStream
    {
        public bool CanWrite => true;

        public void WriteText(string text) { }

        public void WriteTextLine(string text) { }

        public void Flush() { }
    }

    private sealed class TextWriterStream(TextWriter writer) : IToastStream
    {
        public bool CanWrite => true;

        public void WriteText(string text) => writer.Write(text);

        public void WriteTextLine(string text) => writer.WriteLine(text);

        public void Flush() => writer.Flush();

        public async ValueTask WriteTextAsync(string text, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteAsync(text.AsMemory(), cancellationToken);
        }

        public async ValueTask WriteTextLineAsync(string text, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(text.AsMemory(), cancellationToken);
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken)
            => new(writer.FlushAsync(cancellationToken));
    }

    private sealed class CompositeStream(IReadOnlyList<IToastStream> destinations) : IToastStream
    {
        public bool CanWrite => destinations.Any(destination => destination.CanWrite);

        public void WriteText(string text)
        {
            foreach (var destination in destinations) { destination.WriteText(text); }
        }

        public void WriteTextLine(string text)
        {
            foreach (var destination in destinations) { destination.WriteTextLine(text); }
        }

        public void Flush()
        {
            foreach (var destination in destinations) { destination.Flush(); }
        }

        public async ValueTask WriteTextLineAsync(string text, CancellationToken cancellationToken)
        {
            foreach (var destination in destinations)
            {
                await destination.WriteTextLineAsync(text, cancellationToken);
            }
        }

        public async ValueTask FlushAsync(CancellationToken cancellationToken)
        {
            foreach (var destination in destinations)
            {
                await destination.FlushAsync(cancellationToken);
            }
        }
    }
}
