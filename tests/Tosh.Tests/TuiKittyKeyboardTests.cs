using Tosh.Tui.Rendering;

namespace Tosh.Tests;

/// <summary>
/// Telling keys apart that a terminal otherwise sends the same bytes for.
/// </summary>
/// <remarks>
/// <para>
/// Enter and Ctrl+Enter are one byte, so they cannot be distinguished — which made a
/// documented behaviour unreachable: a multiline field inserts a newline on Enter and
/// submits on Ctrl+Enter, and the second branch could never be taken. The field could not be
/// submitted from the keyboard at all. Shift+Tab hides behind Tab the same way.
/// </para>
/// <para>
/// The protocol replaces those with <c>CSI codepoint ; modifiers u</c>. The mapping back is
/// where the care is: nothing downstream should be able to tell which way a key arrived.
/// </para>
/// </remarks>
public sealed class TuiKittyKeyboardTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] entries)
    {
        var map = entries.ToDictionary(e => e.Name, e => e.Value, StringComparer.Ordinal);

        return name => map.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>The sequences push and pop rather than set and clear.</summary>
    /// <remarks>
    /// The terminal keeps a stack of keyboard flags, so popping restores whatever the shell
    /// around the screen had asked for instead of assuming it was nothing.
    /// </remarks>
    [Fact]
    public void The_flags_are_pushed_and_popped()
    {
        Assert.Equal("\x1b[>1u", TuiKittyKeyboard.Enable);
        Assert.Equal("\x1b[<u", TuiKittyKeyboard.Disable);
    }

    /// <summary>An explicit setting is the last word; otherwise the terminal decides.</summary>
    /// <remarks>
    /// This used to be the opt-out alone, so the request went to every terminal — including
    /// the multiplexers that do not relay it and the consoles that cannot honour it, where
    /// what reached the screen was the request itself.
    /// </remarks>
    [Fact]
    public void An_explicit_setting_wins_over_what_the_terminal_says()
    {
        Assert.False(TuiKittyKeyboard.Detect(Env(("TOSH_TUI_KITTY_KEYS", "0"))));
        Assert.False(TuiKittyKeyboard.Detect(Env(("TOSH_TUI_KITTY_KEYS", "off"))));
        Assert.True(TuiKittyKeyboard.Detect(Env(("TOSH_TUI_KITTY_KEYS", "1"), ("TERM", "linux"))));
    }

    [Fact]
    public void A_windowed_terminal_is_asked_and_a_console_is_not()
    {
        Assert.True(TuiKittyKeyboard.Detect(Env(("TERM", "xterm-256color"), ("DISPLAY", ":0"))));
        Assert.False(TuiKittyKeyboard.Detect(Env(("TERM", "linux"))));
        Assert.False(TuiKittyKeyboard.Detect(Env(("TMUX", "/tmp/t"))));
    }

    /// <summary>
    /// The wire value is one greater than the bitmask.
    /// </summary>
    /// <remarks>
    /// So an unmodified key reports 1, and a missing parameter means the same as 1. Reading
    /// it as the mask directly would make every plain key look like Shift.
    /// </remarks>
    [Theory]
    [InlineData(1, (ConsoleModifiers)0)]
    [InlineData(0, (ConsoleModifiers)0)]
    [InlineData(2, ConsoleModifiers.Shift)]
    [InlineData(3, ConsoleModifiers.Alt)]
    [InlineData(5, ConsoleModifiers.Control)]
    [InlineData(6, ConsoleModifiers.Control | ConsoleModifiers.Shift)]
    [InlineData(7, ConsoleModifiers.Control | ConsoleModifiers.Alt)]
    public void Modifiers_are_one_greater_than_the_mask(int parameter, ConsoleModifiers expected)
        => Assert.Equal(expected, TuiKittyKeyboard.Modifiers(parameter));

    /// <summary>
    /// The pair this exists for.
    /// </summary>
    [Fact]
    public void Enter_and_control_enter_are_different_keys()
    {
        var plain = TuiKittyKeyboard.ToKey(13, TuiKittyKeyboard.Modifiers(1));
        var control = TuiKittyKeyboard.ToKey(13, TuiKittyKeyboard.Modifiers(5));

        Assert.Equal(ConsoleKey.Enter, plain.Key);
        Assert.Equal(ConsoleKey.Enter, control.Key);
        Assert.Equal((ConsoleModifiers)0, plain.Modifiers);
        Assert.Equal(ConsoleModifiers.Control, control.Modifiers);
    }

    /// <summary>And the other pair the collision hid.</summary>
    [Fact]
    public void Tab_and_shift_tab_are_different_keys()
    {
        Assert.Equal((ConsoleModifiers)0, TuiKittyKeyboard.ToKey(9, TuiKittyKeyboard.Modifiers(1)).Modifiers);
        Assert.Equal(ConsoleModifiers.Shift, TuiKittyKeyboard.ToKey(9, TuiKittyKeyboard.Modifiers(2)).Modifiers);
    }

    /// <summary>
    /// A Ctrl+letter report rebuilds exactly what a terminal without the protocol sends.
    /// </summary>
    /// <remarks>
    /// This is the assertion that keeps every existing shortcut working. Without the
    /// protocol, .NET reports Ctrl+Q as <c>Key=Q, KeyChar=17, Modifiers=Control</c> — the
    /// control character, not the letter. The wire carries the *unmodified* codepoint
    /// (<c>113</c>, 'q'), so the control character has to be derived rather than read off.
    /// Shortcuts match on either the character or the key, so both must come back right.
    /// </remarks>
    [Theory]
    [InlineData('q', ConsoleKey.Q, 17)]
    [InlineData('a', ConsoleKey.A, 1)]
    [InlineData('c', ConsoleKey.C, 3)]
    public void Control_letters_arrive_as_they_always_did(char letter, ConsoleKey key, int character)
    {
        var pressed = TuiKittyKeyboard.ToKey(letter, TuiKittyKeyboard.Modifiers(5));

        Assert.Equal(key, pressed.Key);
        Assert.Equal((char)character, pressed.KeyChar);
        Assert.Equal(ConsoleModifiers.Control, pressed.Modifiers);
    }

    /// <summary>An unmodified letter is still its own character.</summary>
    [Fact]
    public void A_plain_letter_keeps_its_character()
    {
        var pressed = TuiKittyKeyboard.ToKey('a', TuiKittyKeyboard.Modifiers(1));

        Assert.Equal('a', pressed.KeyChar);
        Assert.Equal(ConsoleKey.A, pressed.Key);
        Assert.Equal((ConsoleModifiers)0, pressed.Modifiers);
    }

    /// <summary>The named keys keep the characters the rest of the framework expects.</summary>
    [Theory]
    [InlineData(13, ConsoleKey.Enter, '\r')]
    [InlineData(9, ConsoleKey.Tab, '\t')]
    [InlineData(27, ConsoleKey.Escape, '\x1b')]
    [InlineData(127, ConsoleKey.Backspace, '\b')]
    [InlineData(32, ConsoleKey.Spacebar, ' ')]
    public void Named_keys_map_to_their_console_key(int codepoint, ConsoleKey key, char character)
    {
        var pressed = TuiKittyKeyboard.ToKey(codepoint, TuiKittyKeyboard.Modifiers(1));

        Assert.Equal(key, pressed.Key);
        Assert.Equal(character, pressed.KeyChar);
    }
}
