
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
}
