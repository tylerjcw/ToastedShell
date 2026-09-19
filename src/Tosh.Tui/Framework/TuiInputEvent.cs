namespace Tosh.Tui;

/// <summary>
/// Represents a unified input event from the terminal — either a key press or a mouse action.
/// Replaces the previous <see cref="ConsoleKeyInfo"/>-only input path so every screen
/// and widget can respond to both keyboard and mouse in a single dispatch.
/// </summary>
public readonly struct TuiInputEvent
{
    private TuiInputEvent(ConsoleKeyInfo key)
    {
        Kind = TuiInputEventKind.Key;
        Key = key;
        Mouse = default;
        Text = string.Empty;
    }

    private TuiInputEvent(TuiMouseEvent mouse)
    {
        Kind = TuiInputEventKind.Mouse;
        Key = default;
        Mouse = mouse;
        Text = string.Empty;
    }

    private TuiInputEvent(string text)
    {
        Kind = TuiInputEventKind.Paste;
        Key = default;
        Mouse = default;
        Text = text;
    }

    public TuiInputEventKind Kind { get; }

    public ConsoleKeyInfo Key { get; }

    public TuiMouseEvent Mouse { get; }

    /// <summary>What was pasted, for a <see cref="TuiInputEventKind.Paste"/> event.</summary>
    /// <remarks>
    /// Arrives whole rather than as the keystrokes it resembles. A pasted newline is a
    /// character in someone's clipboard, not a press of Enter, and a terminal that brackets
    /// its pastes is telling us which of the two this is — see <see cref="TuiBracketedPaste"/>.
    /// </remarks>
    public string Text { get; }

    public bool IsKey => Kind == TuiInputEventKind.Key;

    public bool IsMouse => Kind == TuiInputEventKind.Mouse;

    public bool IsPaste => Kind == TuiInputEventKind.Paste;

    public static TuiInputEvent FromKey(ConsoleKeyInfo key) => new(key);

    public static TuiInputEvent FromMouse(TuiMouseEvent mouse) => new(mouse);

    /// <summary>Text the reader arrived at all at once.</summary>
    public static TuiInputEvent FromPaste(string text) => new(text ?? string.Empty);
}

public enum TuiInputEventKind
{
    Key,
    Mouse,

    /// <summary>Text that arrived as a paste rather than as typing.</summary>
    Paste,
}
