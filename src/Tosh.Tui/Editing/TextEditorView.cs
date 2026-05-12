namespace Tosh.Tui.Editing;

/// <summary>
/// A viewport over a <see cref="TextBuffer"/>: tracks the visible top line and
/// horizontal scroll offset. Use <see cref="EnsureCursorVisible"/> after every
/// buffer mutation or cursor move to keep the cursor in view.
/// </summary>
public sealed class TextEditorView
{
    public TextEditorView(TextBuffer buffer)
    {
        Buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
    }

    public TextBuffer Buffer { get; }

    /// <summary>First buffer line shown at the top of the viewport.</summary>
    public int ScrollLine { get; private set; }

    /// <summary>First visible column (for horizontal scroll on long lines).</summary>
    public int ScrollColumn { get; private set; }

    /// <summary>Viewport size in cells. Updated by the renderer on each frame.</summary>
    public int ViewportWidth { get; private set; }

    public int ViewportHeight { get; private set; }

    public void SetViewportSize(int width, int height)
    {
        ViewportWidth = Math.Max(0, width);
        ViewportHeight = Math.Max(0, height);
    }

    /// <summary>Adjusts scroll so the buffer cursor is on screen.</summary>
    public void EnsureCursorVisible()
    {
        if (ViewportHeight <= 0 || ViewportWidth <= 0)
            return;

        var cursor = Buffer.Cursor;

        if (cursor.Line < ScrollLine)
            ScrollLine = cursor.Line;
        else if (cursor.Line >= ScrollLine + ViewportHeight)
            ScrollLine = cursor.Line - ViewportHeight + 1;

        if (cursor.Column < ScrollColumn)
            ScrollColumn = cursor.Column;
        else if (cursor.Column >= ScrollColumn + ViewportWidth)
            ScrollColumn = cursor.Column - ViewportWidth + 1;

        ScrollLine = Math.Max(0, ScrollLine);
        ScrollColumn = Math.Max(0, ScrollColumn);
    }

    /// <summary>Cursor position translated to viewport-relative (row, col).</summary>
    public (int Row, int Column) GetCursorScreenPosition()
    {
        var c = Buffer.Cursor;
        return (c.Line - ScrollLine, c.Column - ScrollColumn);
    }

    /// <summary>
    /// Viewport-relative positions of every extra caret currently on screen,
    /// in document order. Excludes the primary caret. Off-screen carets are
    /// omitted.
    /// </summary>
    public IReadOnlyList<(int Row, int Column)> GetExtraCursorScreenPositions()
    {
        if (Buffer.ExtraCaretCount == 0) return Array.Empty<(int, int)>();
        var primary = Buffer.Cursor;
        var result = new List<(int, int)>(Buffer.ExtraCaretCount);
        foreach (var c in Buffer.AllCarets)
        {
            if (c == primary) continue;
            var row = c.Line - ScrollLine;
            var col = c.Column - ScrollColumn;
            if (row < 0 || row >= ViewportHeight) continue;
            if (col < 0 || col >= ViewportWidth) continue;
            result.Add((row, col));
        }
        return result;
    }

    /// <summary>
    /// Scrolls the viewport vertically by <paramref name="delta"/> lines.
    /// Positive scrolls down (reveals later lines). The cursor is pulled
    /// along so it stays inside the visible viewport — otherwise
    /// <see cref="EnsureCursorVisible"/> would snap the scroll back as
    /// soon as the cursor fell off-screen.
    /// </summary>
    public void ScrollBy(int delta)
    {
        var max = Math.Max(0, Buffer.LineCount - 1);
        ScrollLine = Math.Clamp(ScrollLine + delta, 0, max);

        if (ViewportHeight <= 0) return;

        var cursor = Buffer.Cursor;
        var top = ScrollLine;
        var bottom = Math.Min(max, ScrollLine + ViewportHeight - 1);
        if (cursor.Line < top)
            Buffer.MoveCursor(new TextLocation(top, cursor.Column));
        else if (cursor.Line > bottom)
            Buffer.MoveCursor(new TextLocation(bottom, cursor.Column));
    }
}
