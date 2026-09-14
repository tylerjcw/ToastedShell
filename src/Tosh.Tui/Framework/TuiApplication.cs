
using Tosh.Tui.Rendering;

namespace Tosh.Tui;

/// <summary>Runs a screen against a terminal.</summary>
public static class TuiApplication
{
    private const string EnterAlternateScreen = "\u001b[?1049h";
    private const string ExitAlternateScreen = "\u001b[?1049l";
    private const string HideCursor = "\u001b[?25l";
    private const string ShowCursor = "\u001b[?25h";
    private const string EnableSgrMouse = "\u001b[?1000h\u001b[?1006h";
    private const string DisableSgrMouse = "\u001b[?1000l\u001b[?1006l";
    private const int MaxInputBurst = 512;

    /// <summary>
    /// How long the loop waits for a keystroke before looking at anything else.
    /// </summary>
    /// <remarks>
    /// The console gives no way to wait on the keyboard and on something else at once —
    /// there is no select(2) behind <c>Console.ReadKey</c> — so a screen with a source or
    /// an interval waits in slices and checks both ends each time round. Short enough that
    /// an arriving value is on screen before anyone notices, long enough that an idle
    /// screen is not a busy loop.
    /// </remarks>
    private static readonly TimeSpan WaitSlice = TimeSpan.FromMilliseconds(25);

    public static void Run(ITuiHost host, ITuiScreen screen)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(screen);

        if (!host.IsInteractive)
        {
            throw new InvalidOperationException("This interactive browser requires a real terminal.");
        }

        // Owns the terminal modes and restores them even if the process is signalled.
        using var session = TuiTerminalSession.Enter(host);

        TuiBuffer? presented = null;
        var failure = new TuiHandlerFailureReporter();

        try
        {
            while (true)
            {
                var size = host.TryGetSize() ?? new TuiSize(80, 25);
                var frame = screen.Render(size);

                failure.Draw(frame.Buffer);

                // Only what changed since the last frame reaches the terminal.
                host.Write(TuiTerminalWriter.Present(presented, frame.Buffer));
                presented = frame.Buffer;

                var refresh = screen.RefreshInterval;
                var wake = screen.Wake;

                if (refresh is null && wake is null)
                {
                    // Nothing changes without the user, so block rather than spin.
                    if (failure.Guard(() => ProcessInputBatch(host, screen, host.ReadInput()))
                        == TuiScreenResult.Exit)
                    {
                        break;
                    }

                    continue;
                }

                if (WaitForSomething(host, screen, failure, wake, refresh) == TuiScreenResult.Exit)
                {
                    break;
                }
            }
        }
        finally
        {
            // Pictures outlive the frame that asked for them, so a screen that ends with
            // one up would leave it painted over the shell the reader is handed back.
            if (presented?.Placements.Count > 0)
            {
                host.Write(TuiGraphics.DeleteAll());
            }

            session.Restore();

            // A screen with sources has tasks reading them, and they outlive the loop
            // unless the screen is told the loop has gone.
            (screen as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Waits for whichever comes first: a keystroke, something posted from another thread,
    /// or the refresh interval elapsing.
    /// </summary>
    private static TuiScreenResult WaitForSomething(
        ITuiHost host,
        ITuiScreen screen,
        TuiHandlerFailureReporter failure,
        TuiWake? wake,
        TimeSpan? refresh)
    {
        var deadline = refresh is { } every
            ? Environment.TickCount64 + (long)every.TotalMilliseconds
            : long.MaxValue;

        while (true)
        {
            // Asked before the keyboard, because work posted while the last frame was being
            // drawn is already waiting and should not sit through a slice first.
            if (wake is not null && wake.TryTake())
            {
                return failure.Guard(() =>
                {
                    wake.Drain();
                    return TuiScreenResult.Continue;
                });
            }

            var remaining = deadline - Environment.TickCount64;

            if (remaining <= 0)
            {
                // The interval elapsed with nothing else happening: let the screen
                // re-sample, then redraw.
                return failure.Guard(screen.Tick);
            }

            var slice = TimeSpan.FromMilliseconds(Math.Min(remaining, (long)WaitSlice.TotalMilliseconds));

            if (host.TryReadInput(slice, out var input))
            {
                return failure.Guard(() => ProcessInputBatch(host, screen, input));
            }
        }
    }

    public static TuiScreenResult ProcessInputBatch(
        ITuiHost host,
        ITuiScreen screen,
        TuiInputEvent firstInput,
        int maxAdditionalInputs = MaxInputBurst)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(screen);

        if (screen.HandleInput(firstInput) == TuiScreenResult.Exit)
        {
            return TuiScreenResult.Exit;
        }

        for (var index = 0; index < maxAdditionalInputs; index += 1)
        {
            if (!host.TryReadPendingInput(out var pendingInput))
            {
                break;
            }

            if (screen.HandleInput(pendingInput) == TuiScreenResult.Exit)
            {
                return TuiScreenResult.Exit;
            }
        }

        return TuiScreenResult.Continue;
    }
}
