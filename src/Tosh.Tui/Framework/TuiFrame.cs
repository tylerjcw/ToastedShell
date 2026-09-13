using Tosh.Tui.Rendering;

namespace Tosh.Tui;

/// <summary>One rendered frame: a grid of cells.</summary>
/// <remarks>
/// <para>
/// A frame was a string, which is why nothing could be drawn over anything else and why
/// the whole screen had to be repainted to change one character. A dialog was spliced into
/// the lines it happened to cover, a mouse click was tested against rectangles the screen
/// kept in fields, and a clock ticking in a corner cleared and rewrote the terminal
/// (<c>TUI-0001</c>).
/// </para>
/// <para>
/// It carried both forms for a while, so the seven screens could move across one at a time
/// rather than in a single change. They all have, so the string is gone: a frame is cells,
/// and the terminal writer sends only the ones that changed.
/// </para>
/// </remarks>
public sealed record TuiFrame
{
    public TuiFrame(TuiBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        Buffer = buffer;
    }

    /// <summary>The cells.</summary>
    public TuiBuffer Buffer { get; }

    /// <summary>What the reader sees, as rows of text with no escapes in the way.</summary>
    /// <remarks>
    /// A caller that wants to read a frame — a test, a log, a diff — should not have to
    /// reach through the grid to do it.
    /// </remarks>
    public string ToPlainText()
        => string.Join('\n', Enumerable
            .Range(0, Buffer.Height)
            .Select(row => Buffer.RowText(row).TrimEnd()));
}
