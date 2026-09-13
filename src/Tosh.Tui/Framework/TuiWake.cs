using System.Collections.Concurrent;

namespace Tosh.Tui;

/// <summary>
/// The way into the render loop from another thread.
/// </summary>
/// <remarks>
/// <para>
/// A live screen could only be driven by a clock: <see cref="ITuiScreen.RefreshInterval"/>
/// and a <see cref="ITuiScreen.Tick"/> that fires when it elapses. That is the right shape
/// for a sample — CPU usage is a measurement, not an event — and the wrong one for
/// everything else a shell shows. A log being tailed, a build emitting lines, a download
/// reporting progress: those arrive when they arrive, and polling them means either a
/// short interval that burns work re-reading nothing or a long one that lags the event
/// (<c>TUI-0008</c>).
/// </para>
/// <para>
/// Work is <em>posted</em> rather than run where it was produced, and the loop runs it on
/// its own thread between frames. So a handler folding an arriving value into a widget can
/// never race the redraw that draws it, and a script author does not have to know there
/// was another thread at all.
/// </para>
/// <para>
/// Redraws coalesce. A thousand values arriving between two frames run as a thousand small
/// handlers and produce one frame, because the signal is a flag rather than a count.
/// </para>
/// </remarks>
public sealed class TuiWake
{
    /// <summary>
    /// How much posted work runs before the loop draws again.
    /// </summary>
    /// <remarks>
    /// A source producing faster than the terminal can draw would otherwise hold the loop
    /// in the drain forever and the screen would never repaint. Anything left over keeps
    /// the signal raised, so the next pass carries on where this one stopped.
    /// </remarks>
    internal const int DrainLimit = 256;

    private readonly ConcurrentQueue<Action> _pending = new();
    private readonly ManualResetEventSlim _raised = new(initialState: false);

    /// <summary>Asks the loop to draw again, without giving it anything to run.</summary>
    /// <remarks>
    /// For a producer that has already changed something the screen reads — a variable a
    /// pull binding will re-read — and only needs the frame.
    /// </remarks>
    public void Signal() => _raised.Set();

    /// <summary>Runs <paramref name="work"/> on the loop's thread, then draws.</summary>
    public void Post(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        _pending.Enqueue(work);
        _raised.Set();
    }

    /// <summary>Whether anything has asked for a frame since the last time this was asked.</summary>
    internal bool TryTake()
    {
        if (!_raised.IsSet)
        {
            return false;
        }

        // Lowered before the queue is drained, not after: a value posted while the drain
        // runs must raise it again rather than be swallowed by the reset that follows.
        _raised.Reset();
        return true;
    }

    /// <summary>Runs what was posted, up to the drain limit. On the loop's thread.</summary>
    internal void Drain()
    {
        for (var count = 0; count < DrainLimit; count += 1)
        {
            if (!_pending.TryDequeue(out var work))
            {
                return;
            }

            work();
        }

        if (!_pending.IsEmpty)
        {
            _raised.Set();
        }
    }
}
