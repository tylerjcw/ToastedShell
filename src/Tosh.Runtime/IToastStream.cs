namespace Tosh.Runtime;

/// <summary>
/// A destination Tōast can write text to — a file, a pipe, a buffer, or a session's
/// terminal.
/// </summary>
/// <remarks>
/// <para>
/// `TOAST-0015`, Phase A. Redirection worked by swapping the *shell session's*
/// <see cref="System.IO.TextWriter"/> — save <c>Runtime.Output</c>, put a composite writer
/// in its place, restore in a <c>finally</c>. It works, and it means a Tōast program that
/// writes <c>run-report out&gt; out.txt</c> needs a shell's stdout to exist before it can
/// redirect away from it.
/// </para>
/// <para>
/// A `no_clr` program has no session and no terminal. Redirection has to target a value
/// the *language* owns, with the shell's writer being one possible destination among files,
/// pipes and buffers rather than the thing being mutated.
/// </para>
/// <para>
/// The shape is taken from <see cref="ManagedFileHandle"/> rather than invented:
/// <c>WriteText</c>, <c>WriteTextLine</c> and <c>Flush</c> are what it already had, and it
/// already carried modes, encodings, seeking and a handle registry. The concept existed;
/// what was missing was that redirection and the file commands targeted different things.
/// </para>
/// </remarks>
public interface IToastStream
{
    /// <summary>Whether this destination currently accepts writes.</summary>
    bool CanWrite { get; }

    /// <summary>Writes <paramref name="text"/> with no line terminator.</summary>
    void WriteText(string text);

    /// <summary>Writes <paramref name="text"/> followed by a line terminator.</summary>
    void WriteTextLine(string text);

    /// <summary>Pushes buffered text to the destination.</summary>
    void Flush();

    /// <inheritdoc cref="WriteText"/>
    ValueTask WriteTextAsync(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WriteText(text);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc cref="WriteTextLine"/>
    ValueTask WriteTextLineAsync(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WriteTextLine(text);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc cref="Flush"/>
    ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Flush();
        return ValueTask.CompletedTask;
    }
}
