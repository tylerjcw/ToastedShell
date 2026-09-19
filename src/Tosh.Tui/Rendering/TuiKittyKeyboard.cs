namespace Tosh.Tui.Rendering;

/// <summary>
/// Asking the terminal to describe keys unambiguously.
/// </summary>
/// <remarks>
/// <para>
/// A terminal sends one byte for Enter and the same byte for Ctrl+Enter, so the two cannot
/// be told apart. That is not a curiosity: a multiline text field is written to insert a
/// newline on Enter and submit on Ctrl+Enter, and without this the second branch is
/// unreachable — the field cannot be submitted from the keyboard at all. The same collision
/// hides Shift+Tab behind Tab.
/// </para>
/// <para>
/// The Kitty keyboard protocol replaces those bytes with <c>CSI codepoint ; modifiers u</c>,
/// which says which key and which modifiers. Only the first flag is asked for —
/// "disambiguate escape codes" — because it fixes exactly this and leaves ordinary typing
/// alone. The richer flags report every press and release, which nothing here wants.
/// </para>
/// <para>
/// Pushed and popped rather than set and cleared: the protocol keeps a stack, so a screen
/// that exits restores whatever the shell around it had asked for instead of assuming it was
/// nothing.
/// </para>
/// </remarks>
public static class TuiKittyKeyboard
{
    /// <summary>Push "disambiguate escape codes" onto the terminal's flag stack.</summary>
    public const string Enable = "\x1b[>1u";

    /// <summary>Pop it again, restoring what the shell around this screen had asked for.</summary>
    public const string Disable = "\x1b[<u";

    /// <summary>Whether to ask, by the environment's account.</summary>
    /// <remarks>
    /// An opt-out, like the other protocols: a terminal that does not know the sequence
    /// ignores it, so the default that helps most readers is the one that needs no setting.
    /// </remarks>
    public static bool Detect(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return environment("TOSH_TUI_KITTY_KEYS") is not ("0" or "off" or "false" or "no");
    }

    /// <summary>
    /// The modifiers a Kitty parameter encodes.
    /// </summary>
    /// <remarks>
    /// The wire value is one greater than the bitmask, so an unmodified key reports 1 and
    /// nothing reports 0. A missing parameter means the same as 1.
    /// </remarks>
    public static ConsoleModifiers Modifiers(int parameter)
    {
        var bits = parameter <= 0 ? 0 : parameter - 1;
        var modifiers = (ConsoleModifiers)0;

        if ((bits & 1) != 0) { modifiers |= ConsoleModifiers.Shift; }
        if ((bits & 2) != 0) { modifiers |= ConsoleModifiers.Alt; }
        if ((bits & 4) != 0) { modifiers |= ConsoleModifiers.Control; }

        return modifiers;
    }

    /// <summary>
    /// The key a Kitty report describes, as the rest of the framework expects to see it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The point is that nothing downstream has to know this protocol exists. A terminal
    /// without it delivers Ctrl+A as byte 1, which .NET reports as
    /// <c>Key=A, KeyChar=1, Modifiers=Control</c>; with it the same press arrives as
    /// <c>CSI 97;5u</c>, and this rebuilds that exact answer. Shortcuts that already match
    /// on either the character or the key keep working, unchanged.
    /// </para>
    /// <para>
    /// The control character is derived rather than taken from the wire because the wire
    /// carries the *unmodified* codepoint — <c>97</c> is 'a', not 1.
    /// </para>
    /// </remarks>
    public static ConsoleKeyInfo ToKey(int codepoint, ConsoleModifiers modifiers)
    {
        var shift = (modifiers & ConsoleModifiers.Shift) != 0;
        var alt = (modifiers & ConsoleModifiers.Alt) != 0;
        var control = (modifiers & ConsoleModifiers.Control) != 0;

        switch (codepoint)
        {
            case 13: return new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift, alt, control);
            case 9: return new ConsoleKeyInfo('\t', ConsoleKey.Tab, shift, alt, control);
            case 27: return new ConsoleKeyInfo('\x1b', ConsoleKey.Escape, shift, alt, control);
            case 127: return new ConsoleKeyInfo('\b', ConsoleKey.Backspace, shift, alt, control);
            case 32: return new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, shift, alt, control);
        }

        if (codepoint is >= 'a' and <= 'z')
        {
            var letter = (ConsoleKey)(ConsoleKey.A + (codepoint - 'a'));

            // Ctrl+letter is the control character it has always been, so a shortcut
            // matching on `KeyChar == '\x11'` still matches.
            var character = control ? (char)(codepoint - 'a' + 1) : (char)codepoint;

            return new ConsoleKeyInfo(character, letter, shift, alt, control);
        }

        if (codepoint is >= 'A' and <= 'Z')
        {
            var letter = (ConsoleKey)(ConsoleKey.A + (codepoint - 'A'));
            var character = control ? (char)(codepoint - 'A' + 1) : (char)codepoint;

            return new ConsoleKeyInfo(character, letter, shift: true, alt, control);
        }

        if (codepoint is >= '0' and <= '9')
        {
            return new ConsoleKeyInfo(
                (char)codepoint,
                (ConsoleKey)(ConsoleKey.D0 + (codepoint - '0')),
                shift,
                alt,
                control);
        }

        // Anything else keeps its character and says nothing about which key produced it,
        // which is what a terminal without the protocol would also have said.
        return codepoint is > 0 and <= char.MaxValue
            ? new ConsoleKeyInfo((char)codepoint, ConsoleKey.None, shift, alt, control)
            : new ConsoleKeyInfo('\0', ConsoleKey.None, shift, alt, control);
    }
}
