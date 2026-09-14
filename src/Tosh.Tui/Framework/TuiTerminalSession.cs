using System.Runtime.InteropServices;

namespace Tosh.Tui;

/// <summary>
/// Owns the terminal modes a full-screen application switches on, and guarantees they
/// are switched off again.
/// </summary>
/// <remarks>
/// <para>
/// A TUI puts the terminal into the alternate screen, hides the cursor and turns on
/// mouse reporting. A <c>finally</c> covers an exception, and covers nothing else: a
/// <c>SIGINT</c>, <c>SIGTERM</c> or <c>SIGHUP</c> ends the process without running it,
/// and the user is left in the alternate screen with an invisible cursor and mouse
/// reporting still on — their next prompt is not there. TōSh is a login shell, so that
/// is a different class of bug from drawing a box wrong (<c>TUI-0010</c>).
/// </para>
/// <para>
/// Restoration is therefore registered with the operating system as well as with the
/// runtime, and is idempotent: whichever path reaches it first wins and the rest do
/// nothing.
/// </para>
/// </remarks>
public sealed class TuiTerminalSession : IDisposable
{
    private const string EnterAlternateScreen = "\u001b[?1049h";
    private const string ExitAlternateScreen = "\u001b[?1049l";
    private const string HideCursor = "\u001b[?25l";
    private const string ShowCursor = "\u001b[?25h";
    private const string EnableSgrMouse = "\u001b[?1000h\u001b[?1006h";
    private const string DisableSgrMouse = "\u001b[?1000l\u001b[?1006l";
    private const string ResetStyles = "\u001b[0m";

    private readonly ITuiHost _host;
    private readonly List<IDisposable> _signalRegistrations = [];
    private readonly ConsoleCancelEventHandler _cancelHandler;
    private int _restored;

    private TuiTerminalSession(ITuiHost host)
    {
        _host = host;

        // The escapes go out before the handlers are installed, so a signal arriving
        // during setup finds a session that already knows what it has to undo.
        _host.Write(EnterAlternateScreen);
        _host.Write(HideCursor);
        _host.Write(EnableSgrMouse);

        // A note for whoever comes next, in case this process never gets to clear it.
        TuiTerminalRepair.Taken();

        _cancelHandler = (_, eventArgs) =>
        {
            Restore();

            // Not cancelled: a signal the process is meant to die from should still kill
            // it. The only job here is to hand back a usable terminal on the way out.
            eventArgs.Cancel = false;
        };

        Console.CancelKeyPress += _cancelHandler;

        // SIGINT is registered directly as well as through CancelKeyPress. The event is
        // raised for a Ctrl+C typed at the terminal; a SIGINT delivered by other means —
        // a kill, a process group signal from a parent — does not reliably reach it
        // while the loop is blocked reading a key. Restore is idempotent, so being
        // called twice costs nothing.
        Register(PosixSignal.SIGINT);
        Register(PosixSignal.SIGTERM);
        Register(PosixSignal.SIGQUIT);

        // SIGHUP is registered for completeness, but it means the terminal has gone:
        // there is usually nothing left to restore it to, and the write is expected to
        // fail. See the IOException case in Restore.
        Register(PosixSignal.SIGHUP);
    }

    /// <summary>Switches the terminal into full-screen mode.</summary>
    public static TuiTerminalSession Enter(ITuiHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return new TuiTerminalSession(host);
    }

    private void Register(PosixSignal signal)
    {
        try
        {
            _signalRegistrations.Add(PosixSignalRegistration.Create(signal, context =>
            {
                Restore();

                // The default action still applies: a signal the process is meant to die
                // from should kill it. Making the ordering deterministic by calling
                // Environment.Exit here was tried and reverted — running shutdown from a
                // signal handler while other threads hold locks risks a hang, which is a
                // worse failure than the one being fixed. What makes the restore land in
                // time is the urgent write path; see ITuiHost.WriteUrgent.
                context.Cancel = false;
            }));
        }
        catch (PlatformNotSupportedException)
        {
            // A platform without this signal simply does not get that guarantee.
        }
    }

    /// <summary>
    /// Puts the terminal back. Safe to call more than once, and from a signal handler.
    /// </summary>
    public void Restore()
    {
        // Whichever of dispose, Ctrl+C or a signal arrives first does the work.
        if (Interlocked.Exchange(ref _restored, 1) != 0)
        {
            return;
        }

        try
        {
            // One write rather than four: a signal can arrive between them, and half a
            // restoration is worse than none.
            // A picture the terminal was asked to draw outlives the program that asked, so
            // it is taken back here too: a crash during a preview would otherwise leave it
            // painted over the shell the reader is handed back.
            var pictures = Rendering.TuiGraphics.Protocol == Rendering.TuiGraphicsProtocol.Kitty
                ? Rendering.TuiGraphics.DeleteAll()
                : string.Empty;

            _host.WriteUrgent(pictures + DisableSgrMouse + ResetStyles + ShowCursor + ExitAlternateScreen);
        }
        catch (IOException)
        {
            // The terminal has gone — a closed pty on SIGHUP. There is nothing left to
            // restore it to, and throwing out of a signal handler helps no one.
        }
        catch (ObjectDisposedException)
        {
        }

        // After the write, so a crash between the two leaves the note rather than losing it.
        TuiTerminalRepair.Given();
    }

    public void Dispose()
    {
        Restore();

        Console.CancelKeyPress -= _cancelHandler;

        foreach (var registration in _signalRegistrations)
        {
            registration.Dispose();
        }

        _signalRegistrations.Clear();
    }
}
