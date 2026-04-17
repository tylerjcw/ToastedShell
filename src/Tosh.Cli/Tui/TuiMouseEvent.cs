namespace Tosh.Cli.Tui;

/// <summary>
/// Describes a single mouse event decoded from SGR extended mouse protocol sequences.
/// Coordinates are 0-based (column, row) relative to the terminal screen origin.
/// </summary>
internal readonly record struct TuiMouseEvent(
    TuiMouseAction Action,
    TuiMouseButton Button,
    int Column,
    int Row,
    bool Shift,
    bool Alt,
    bool Control)
{
    /// <summary>Tests whether this event falls within the given screen-space rectangle.</summary>
    public bool HitsRect(TuiRect rect) =>
        Column >= rect.Left && Column < rect.Right &&
        Row >= rect.Top && Row < rect.Bottom;

    /// <summary>Returns the column/row offset relative to the rectangle's origin, or null if outside.</summary>
    public (int LocalColumn, int LocalRow)? ToLocal(TuiRect rect) =>
        HitsRect(rect)
            ? (Column - rect.Left, Row - rect.Top)
            : null;
}

internal enum TuiMouseAction
{
    /// <summary>A button was pressed down.</summary>
    Press,

    /// <summary>A button was released.</summary>
    Release,

    /// <summary>The mouse moved while a button was held (drag).</summary>
    Drag,

    /// <summary>The scroll wheel was rotated.</summary>
    Scroll,
}

internal enum TuiMouseButton
{
    Left,
    Middle,
    Right,
    ScrollUp,
    ScrollDown,
    None,
}
