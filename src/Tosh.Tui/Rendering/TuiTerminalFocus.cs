namespace Tosh.Tui.Rendering;

/// <summary>
/// Asking the terminal to say when the window gains or loses focus.
/// </summary>
/// <remarks>
/// <para>
/// A screen otherwise has no idea whether anyone is looking at it. Mode 1004 makes the
/// terminal send <c>CSI I</c> when its window is focused and <c>CSI O</c> when it is not, so
/// a widget can dim what it is showing, stop an animation, or drop a caret that would
/// otherwise blink at an empty chair.
/// </para>
/// <para>
/// Like the other private modes here, the reader must understand the sequences before the
/// mode is asked for: a terminal sending <c>CSI I</c> to something that does not parse it
/// types <c>[I</c> into whatever has focus.
/// </para>
/// <para>
/// The framework asks for the reports and delivers them; it does not itself change what it
/// draws. Skipping the refresh tick while unfocused was the obvious use and is the wrong
/// one — a screen's tick re-samples as well as redraws, so a screen nobody is looking at
/// would quietly stop keeping up with the thing it is watching.
/// </para>
/// </remarks>
public static class TuiTerminalFocus
{
    /// <summary>Ask the terminal to report focus changes.</summary>
    public const string Enable = "\x1b[?1004h";

    /// <summary>Stop, on the way out.</summary>
    public const string Disable = "\x1b[?1004l";

    /// <summary>What the terminal sends when its window gains focus.</summary>
    public const string Gained = "\x1b[I";

    /// <summary>And when it loses it.</summary>
    public const string Lost = "\x1b[O";

    /// <summary>Whether to ask, by the environment's account.</summary>
    /// <param name="terminal">
    /// The terminal as the shell resolved it — what the reader configured over what was
    /// detected. Null works it out from the environment alone.
    /// </param>
    public static bool Detect(Func<string, string?> environment, TerminalProfile? terminal = null)
    {
        ArgumentNullException.ThrowIfNull(environment);

        // The environment variable is the emergency override and stays the last word. Below
        // it the terminal decides, which used to be nothing at all: the request went to every
        // terminal, including the multiplexers that do not relay it and the consoles that
        // cannot honour it, where what reached the screen was the request itself.
        if (environment("TOSH_TUI_FOCUS") is { } told)
        {
            return told is not ("0" or "off" or "false" or "no");
        }

        return (terminal ?? TerminalProfile.Detect(environment)).FocusReporting;
    }
}
