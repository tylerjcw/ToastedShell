
namespace Tosh.Tui;

public interface ITuiHost
{
    bool IsInteractive { get; }

    TuiSize? TryGetSize();

    ConsoleKeyInfo ReadKey(bool intercept = true);

    bool TryReadPendingKey(out ConsoleKeyInfo key, bool intercept = true);

    TuiInputEvent ReadInput();

    bool TryReadPendingInput(out TuiInputEvent inputEvent);

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for input, returning false if none arrived.
    /// </summary>
    /// <remarks>
    /// The default polls <see cref="TryReadPendingInput"/>, because the console gives no way
    /// to wait on stdin with a deadline — there is no select(2) behind
    /// <see cref="Console.ReadKey"/>. A host that can wait properly should override this.
    /// The poll interval is short enough to stay responsive to a keystroke and long enough
    /// that an idle screen is not a busy loop.
    /// </remarks>
    bool TryReadInput(TimeSpan timeout, out TuiInputEvent inputEvent)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

        while (true)
        {
            if (TryReadPendingInput(out inputEvent))
            {
                return true;
            }

            if (Environment.TickCount64 >= deadline)
            {
                inputEvent = default;
                return false;
            }

            Thread.Sleep(15);
        }
    }

    void Write(string text);

    /// <summary>
    /// Writes without taking the console's lock, for use from a signal handler.
    /// </summary>
    /// <remarks>
    /// A signal handler runs on its own thread while the render loop is usually blocked
    /// in <see cref="Console.ReadKey"/>, and .NET serialises console access — so an
    /// ordinary write from the handler waits for a lock the blocked thread is holding,
    /// and the process dies from the signal before the bytes leave. That is the
    /// difference between handing the terminal back and leaving the user staring at an
    /// alternate screen with no cursor (<c>TUI-0010</c>).
    ///
    /// The default is an ordinary write, which is right for any host that is not a real
    /// console and keeps this off the list of things a test host must implement.
    /// </remarks>
    void WriteUrgent(string text) => Write(text);
}
