namespace Tosh.Tui.Rendering;

/// <summary>
/// Asking the terminal to say which text was pasted and which was typed.
/// </summary>
/// <remarks>
/// <para>
/// Without it the two are the same thing. Pasting three lines into a text field delivers
/// the newlines as presses of Enter, so the first line is submitted, and whatever the form
/// did on submit happens twice more with the rest of the clipboard behind it. That is not a
/// key a reader pressed; it is a character in something they copied.
/// </para>
/// <para>
/// Mode 2004 wraps a paste in <c>CSI 200 ~</c> and <c>CSI 201 ~</c>, which is enough to
/// deliver it whole. The reader must understand the brackets before the mode is asked for:
/// a terminal sending them to something that does not parse them types <c>[200~</c> into
/// the field, so enabling this without the parser is worse than leaving it off.
/// </para>
/// </remarks>
public static class TuiBracketedPaste
{
    /// <summary>Ask the terminal to bracket pastes.</summary>
    public const string Enable = "\x1b[?2004h";

    /// <summary>Stop, on the way out, so the shell underneath is left as it was found.</summary>
    public const string Disable = "\x1b[?2004l";

    /// <summary>The sequence a terminal sends before pasted text.</summary>
    public const string Start = "\x1b[200~";

    /// <summary>And after it.</summary>
    public const string Finish = "\x1b[201~";

    /// <summary>Whether to ask, by the environment's account.</summary>
    /// <remarks>
    /// An opt-out, like synchronized output: a terminal that does not know the mode ignores
    /// it, so the default that helps most readers is the one that needs no setting.
    /// </remarks>
    public static bool Detect(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return environment("TOSH_TUI_PASTE") is not ("0" or "off" or "false" or "no");
    }
}
