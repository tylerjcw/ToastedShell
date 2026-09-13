using Tosh.Runtime;
using Tosh.Tui.Rendering;

namespace Tosh.Tui;

/// <summary>One rendered frame: either a grid of cells or, for screens not yet moved
/// across, a block of pre-formatted text.</summary>
/// <remarks>
/// <para>
/// A frame was a string, which is why nothing could be drawn over anything else and why
/// the whole screen had to be repainted to change one character (<c>TUI-0001</c>).
/// Frames are becoming buffers.
/// </para>
/// <para>
/// Both forms are carried at once so screens can move one at a time rather than in a
/// single change across all seven of them. A screen that returns a buffer gets diffed
/// output and can use overlays and a cursor; a screen that returns a string behaves
/// exactly as it did. The string form goes away with <c>TUI-0003</c>.
/// </para>
/// </remarks>
public sealed record TuiFrame
{
    /// <summary>A frame of pre-formatted text, as screens produced before buffers.</summary>
    public TuiFrame(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Content = content;
    }

    /// <summary>A frame of cells.</summary>
    public TuiFrame(TuiBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        Buffer = buffer;
        Content = string.Empty;
    }

    /// <summary>The cells, when this frame was drawn into a buffer.</summary>
    public TuiBuffer? Buffer { get; }

    /// <summary>The text, when this frame was built as a string.</summary>
    public string Content { get; }

    /// <summary>
    /// What the reader sees, whichever form the frame was drawn in.
    /// </summary>
    /// <remarks>
    /// A caller that wants to read a frame — a test, a log, a diff — should not have to
    /// know which half of this record is filled in. Cells answer as rows of text; a string
    /// frame answers with its escape codes removed.
    /// </remarks>
    public string ToPlainText()
        => Buffer is { } buffer
            ? string.Join('\n', Enumerable
                .Range(0, buffer.Height)
                .Select(row => buffer.RowText(row).TrimEnd()))
            : StyledText.StripAnsi(Content);
}
