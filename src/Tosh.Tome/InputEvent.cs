namespace Tosh.Tome;

internal enum InputEventKind
{
    Key,
    MousePress,
    MouseRelease,
    MouseMove,
    MouseWheel,
}

internal enum MouseButton
{
    None,
    Left,
    Middle,
    Right,
}

/// <summary>
/// Discriminated input event surfaced by <see cref="TerminalDriver.ReadEvent"/>.
/// For mouse events <see cref="Row"/> and <see cref="Column"/> are zero-based
/// screen coordinates. <see cref="WheelDelta"/> is +1 for wheel-up, -1 for
/// wheel-down. Modifier flags are populated from the SGR mouse encoding.
/// </summary>
internal readonly record struct InputEvent(
    InputEventKind Kind,
    ConsoleKeyInfo Key,
    MouseButton Button,
    int Row,
    int Column,
    int WheelDelta,
    bool Shift,
    bool Alt,
    bool Ctrl)
{
    public static InputEvent FromKey(ConsoleKeyInfo k) =>
        new(InputEventKind.Key, k, MouseButton.None, 0, 0, 0, false, false, false);
}
