namespace Tosh.Tui;

/// <summary>
/// Notices a terminal a previous TōSh left in full-screen mode, and puts it back.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TuiTerminalSession"/> restores on every path it can reach: a normal exit, an
/// exception, and the signals a process can be asked to die from. It cannot reach
/// <c>SIGKILL</c>, a hard power loss, or a runtime that fails before any of its handlers
/// run — and after one of those the reader is left in the alternate screen with an
/// invisible cursor, which is the failure <c>TUI-0010</c> exists for.
/// </para>
/// <para>
/// So a session leaves a note while it holds the terminal and removes it on the way out. A
/// note still there when TōSh next starts on that same terminal means the last one did not
/// get out, and the repair is worth making. Blindly emitting the escapes at every start
/// would also work and would be wrong: a shell should not write to the terminal on the
/// chance that something is broken.
/// </para>
/// </remarks>
public static class TuiTerminalRepair
{
    /// <summary>What puts a terminal back, whatever state it was left in.</summary>
    /// <remarks>
    /// Includes the graphics delete deliberately: a picture the terminal was asked to draw
    /// outlives the program that asked, so a crash during a preview leaves it painted over
    /// the shell the reader is handed back.
    /// </remarks>
    public const string Sane =
        "\u001b_Ga=d,d=A,q=2;\u001b\\" +          // every picture, off
        "\u001b[?1000l\u001b[?1006l" +             // mouse reporting, off
        "\u001b[?2004l" +                          // bracketed paste, off
        "\u001b[?1004l" +                          // focus reporting, off
        "\u001b[<u" +                              // keyboard flags, popped
        "\u001b[?2026l" +                          // any held frame, released
        "\u001b[0m" +                              // styling, reset
        "\u001b[?25h" +                            // cursor, shown
        "\u001b[?1049l";                           // alternate screen, left

    /// <summary>Where notes are kept.</summary>
    private static string Directory
    {
        get
        {
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");

            return string.IsNullOrWhiteSpace(runtime) ? Path.GetTempPath() : runtime;
        }
    }

    /// <summary>
    /// The note for the terminal this process is attached to, or null if it has none.
    /// </summary>
    /// <remarks>
    /// Keyed by the terminal rather than by the machine, because a crash in one window
    /// should not make every other window repaint itself.
    /// </remarks>
    private static string? Note()
    {
        if (Terminal() is not { } terminal)
        {
            return null;
        }

        var slug = new string([.. terminal.Select(character =>
            char.IsLetterOrDigit(character) ? character : '-')]);

        return Path.Combine(Directory, $"tosh-tui-{slug}.note");
    }

    /// <summary>
    /// Which terminal this process is attached to. Replaceable, so the note-keeping can be
    /// exercised somewhere that has no terminal at all.
    /// </summary>
    internal static Func<string?> Terminal { get; set; } = AttachedTerminal;

    /// <summary>Says that this process has the terminal in full-screen mode.</summary>
    internal static void Taken() => Try(note => File.WriteAllText(note, $"{Environment.ProcessId}"));

    /// <summary>Says that this process has given the terminal back.</summary>
    internal static void Given() => Try(File.Delete);

    private static string? AttachedTerminal()
    {
        try
        {
            // The device behind standard output. A name rather than a number, so two runs
            // on the same terminal agree and two terminals never do.
            var link = new FileInfo("/proc/self/fd/1").LinkTarget;

            if (link is { Length: > 0 } && link.StartsWith("/dev/", StringComparison.Ordinal))
            {
                return link;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        var told = Environment.GetEnvironmentVariable("SSH_TTY");

        return string.IsNullOrWhiteSpace(told) ? null : told;
    }

    /// <summary>
    /// Puts the terminal back if the last TōSh on it did not, and says whether it did.
    /// </summary>
    /// <param name="write">How to reach the terminal.</param>
    public static bool RepairIfNeeded(Action<string> write)
    {
        ArgumentNullException.ThrowIfNull(write);

        try
        {
            if (Note() is not { } note || !File.Exists(note))
            {
                return false;
            }

            File.Delete(note);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        write(Sane);
        return true;
    }

    /// <summary>Puts the terminal back whether or not anything says it is broken.</summary>
    /// <remarks>
    /// What <c>tui reset</c> runs. A reader who can see that their terminal is wrong should
    /// not have to convince anything of it first.
    /// </remarks>
    public static void Repair(Action<string> write)
    {
        ArgumentNullException.ThrowIfNull(write);

        Given();
        write(Sane);
    }

    /// <summary>Does something with the note, or shrugs. Never the caller's problem.</summary>
    private static void Try(Action<string> what)
    {
        try
        {
            if (Note() is { } note)
            {
                what(note);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Not being able to leave a note costs the repair, not the session.
        }
    }
}
