namespace Tosh.Cli.Tui;

/// <summary>
/// Represents a unified input event from the terminal — either a key press or a mouse action.
/// Replaces the previous <see cref="ConsoleKeyInfo"/>-only input path so every screen
/// and widget can respond to both keyboard and mouse in a single dispatch.
/// </summary>
internal readonly struct TuiInputEvent
{
    private TuiInputEvent(ConsoleKeyInfo key)
    {
        Kind = TuiInputEventKind.Key;
        Key = key;
        Mouse = default;
    }

    private TuiInputEvent(TuiMouseEvent mouse)
    {
        Kind = TuiInputEventKind.Mouse;
        Key = default;
        Mouse = mouse;
    }

    public TuiInputEventKind Kind { get; }

    public ConsoleKeyInfo Key { get; }

    public TuiMouseEvent Mouse { get; }

    public bool IsKey => Kind == TuiInputEventKind.Key;

    public bool IsMouse => Kind == TuiInputEventKind.Mouse;

    public static TuiInputEvent FromKey(ConsoleKeyInfo key) => new(key);

    public static TuiInputEvent FromMouse(TuiMouseEvent mouse) => new(mouse);
}

internal enum TuiInputEventKind
{
    Key,
    Mouse,
}
