
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

                if (refresh is null)
                {
                    // Nothing changes without the user, so block rather than spin.
                    if (failure.Guard(() => ProcessInputBatch(host, screen, host.ReadInput()))
                        == TuiScreenResult.Exit)
                    {
                        break;
                    }

                    continue;
                }

                if (host.TryReadInput(refresh.Value, out var timedInput))
                {
                    if (failure.Guard(() => ProcessInputBatch(host, screen, timedInput))
                        == TuiScreenResult.Exit)
                    {
                        break;
                    }

                    continue;
                }

                // The interval elapsed with no input: let the screen re-sample, then redraw.
                if (failure.Guard(screen.Tick) == TuiScreenResult.Exit)
                {
                    break;
                }
            }
        }
        finally
        {
            session.Restore();
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
