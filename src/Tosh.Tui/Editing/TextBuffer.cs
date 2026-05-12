using System.Text;

namespace Tosh.Tui.Editing;

/// <summary>
/// A line-aware mutable text buffer with undo/redo history.
///
/// Stores the document as a list of lines (no trailing '\n' in each entry; the
/// terminator is implicit between successive lines). Edits coalesce small
/// character-level operations into single undo entries so typing a word
/// undoes as one chunk.
///
/// Cursor position is exposed as a <see cref="TextLocation"/> (line + column)
/// and clamped on every mutation; callers never see invalid state.
///
/// This buffer is line-storage-internal — callers should not assume any
/// particular implementation. Future work may swap in a piece table or rope
/// for very large files without changing the API.
/// </summary>
public sealed class TextBuffer
{
    private const int MaxHistoryDepth = 256;

    private readonly List<string> _lines = new() { string.Empty };
    private readonly Stack<Snapshot> _undo = new();
    private readonly Stack<Snapshot> _redo = new();

    private TextLocation _cursor;
    private TextLocation? _selectionAnchor;
    private EditKind _lastEditKind = EditKind.None;

    public TextBuffer() { }

    public TextBuffer(string initialText)
    {
        LoadText(initialText);
    }

    public int LineCount => _lines.Count;

    public TextLocation Cursor => _cursor;

    public bool IsModified { get; private set; }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>
    /// Currently selected range, normalized so Start &lt;= End. Null when nothing
    /// is selected (no anchor set, or anchor equals cursor).
    /// </summary>
    public (TextLocation Start, TextLocation End)? Selection
    {
        get
        {
            if (_selectionAnchor is not TextLocation anchor || anchor == _cursor)
                return null;
            return Compare(anchor, _cursor) <= 0
                ? (anchor, _cursor)
                : (_cursor, anchor);
        }
    }

    public bool HasSelection => Selection is not null;

    /// <summary>Begin/extend a selection. Idempotent if an anchor already exists.</summary>
    public void BeginSelection()
    {
        _selectionAnchor ??= _cursor;
    }

    public void ClearSelection() => _selectionAnchor = null;

    public string GetSelectionText()
    {
        var sel = Selection;
        if (sel is null) return string.Empty;
        var (start, end) = sel.Value;
        return ExtractRange(start, end);
    }

    /// <summary>
    /// Deletes the active selection (if any) and returns the deleted text.
    /// No-op and returns empty string when there is no selection.
    /// </summary>
    public string DeleteSelection()
    {
        var sel = Selection;
        if (sel is null) return string.Empty;
        var (start, end) = sel.Value;
        var deleted = ExtractRange(start, end);

        PushUndo();
        var startLine = _lines[start.Line];
        var endLine = _lines[end.Line];
        var merged = startLine[..start.Column] + endLine[end.Column..];
        _lines[start.Line] = merged;
        for (var i = end.Line; i > start.Line; i--)
            _lines.RemoveAt(i);

        _cursor = start;
        _selectionAnchor = null;
        _lastEditKind = EditKind.None;
        IsModified = true;
        return deleted;
    }

    public string GetLine(int lineIndex) =>
        lineIndex >= 0 && lineIndex < _lines.Count ? _lines[lineIndex] : string.Empty;

    public int GetLineLength(int lineIndex) => GetLine(lineIndex).Length;

    /// <summary>Returns the full document as a single string with '\n' separators.</summary>
    public string GetText() => string.Join('\n', _lines);

    /// <summary>Replaces the entire buffer; resets cursor to (0,0) and clears history.</summary>
    public void LoadText(string text)
    {
        _undo.Clear();
        _redo.Clear();
        _lines.Clear();

        var normalized = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        var parts = normalized.Split('\n');
        foreach (var part in parts)
            _lines.Add(part);

        if (_lines.Count == 0)
            _lines.Add(string.Empty);

        _cursor = default;
        _lastEditKind = EditKind.None;
        IsModified = false;
    }

    /// <summary>Marks the buffer as freshly persisted; does not change content.</summary>
    public void MarkClean() => IsModified = false;

    public void MoveCursor(TextLocation location)
    {
        _cursor = ClampLocation(location);
        _lastEditKind = EditKind.None;
    }

    public void MoveLeft()
    {
        if (_cursor.Column > 0)
            MoveCursor(_cursor with { Column = _cursor.Column - 1 });
        else if (_cursor.Line > 0)
            MoveCursor(new TextLocation(_cursor.Line - 1, GetLineLength(_cursor.Line - 1)));
    }

    public void MoveRight()
    {
        var lineLen = GetLineLength(_cursor.Line);
        if (_cursor.Column < lineLen)
            MoveCursor(_cursor with { Column = _cursor.Column + 1 });
        else if (_cursor.Line < _lines.Count - 1)
            MoveCursor(new TextLocation(_cursor.Line + 1, 0));
    }

    public void MoveUp() => MoveCursor(new TextLocation(_cursor.Line - 1, _cursor.Column));

    public void MoveDown() => MoveCursor(new TextLocation(_cursor.Line + 1, _cursor.Column));

    public void MoveLineStart() => MoveCursor(_cursor with { Column = 0 });

    public void MoveLineEnd() => MoveCursor(_cursor with { Column = GetLineLength(_cursor.Line) });

    /// <summary>Jump to the start of the previous word, crossing line breaks if needed.</summary>
    public void MoveWordLeft()
    {
        var line = _cursor.Line;
        var col = _cursor.Column;
        if (col == 0)
        {
            if (line == 0) return;
            line--;
            col = GetLineLength(line);
            MoveCursor(new TextLocation(line, col));
            return;
        }
        var text = _lines[line];
        // Skip trailing whitespace, then skip the word characters.
        while (col > 0 && IsWordSeparator(text[col - 1])) col--;
        while (col > 0 && !IsWordSeparator(text[col - 1])) col--;
        MoveCursor(new TextLocation(line, col));
    }

    /// <summary>Jump to the start of the next word, crossing line breaks if needed.</summary>
    public void MoveWordRight()
    {
        var line = _cursor.Line;
        var col = _cursor.Column;
        var text = _lines[line];
        if (col >= text.Length)
        {
            if (line >= _lines.Count - 1) return;
            MoveCursor(new TextLocation(line + 1, 0));
            return;
        }
        // Skip the current word, then skip following whitespace.
        while (col < text.Length && !IsWordSeparator(text[col])) col++;
        while (col < text.Length && IsWordSeparator(text[col])) col++;
        MoveCursor(new TextLocation(line, col));
    }

    private static bool IsWordSeparator(char ch) => !char.IsLetterOrDigit(ch) && ch != '_';

    public void InsertChar(char ch)
    {
        if (ch == '\n')
        {
            InsertNewline();
            return;
        }

        if (_lastEditKind != EditKind.InsertChar)
            PushUndo();

        var line = _lines[_cursor.Line];
        _lines[_cursor.Line] = line.Insert(_cursor.Column, ch.ToString());
        _cursor = _cursor with { Column = _cursor.Column + 1 };
        _lastEditKind = EditKind.InsertChar;
        IsModified = true;
    }

    public void InsertText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        PushUndo();
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (var ch in normalized)
        {
            if (ch == '\n')
                InsertNewlineRaw();
            else
                InsertCharRaw(ch);
        }
        _lastEditKind = EditKind.None;
        IsModified = true;
    }

    public void InsertNewline()
    {
        PushUndo();
        InsertNewlineRaw();
        _lastEditKind = EditKind.None;
        IsModified = true;
    }

    public void Backspace()
    {
        if (_cursor.Line == 0 && _cursor.Column == 0)
            return;

        if (_lastEditKind != EditKind.DeleteBack)
            PushUndo();

        if (_cursor.Column > 0)
        {
            var line = _lines[_cursor.Line];
            _lines[_cursor.Line] = line.Remove(_cursor.Column - 1, 1);
            _cursor = _cursor with { Column = _cursor.Column - 1 };
        }
        else
        {
            // Join with previous line.
            var prev = _lines[_cursor.Line - 1];
            var current = _lines[_cursor.Line];
            _lines[_cursor.Line - 1] = prev + current;
            _lines.RemoveAt(_cursor.Line);
            _cursor = new TextLocation(_cursor.Line - 1, prev.Length);
        }

        _lastEditKind = EditKind.DeleteBack;
        IsModified = true;
    }

    public void DeleteForward()
    {
        var lineLen = GetLineLength(_cursor.Line);
        if (_cursor.Column == lineLen && _cursor.Line == _lines.Count - 1)
            return;

        if (_lastEditKind != EditKind.DeleteForward)
            PushUndo();

        if (_cursor.Column < lineLen)
        {
            var line = _lines[_cursor.Line];
            _lines[_cursor.Line] = line.Remove(_cursor.Column, 1);
        }
        else
        {
            // Join with next line.
            var next = _lines[_cursor.Line + 1];
            _lines[_cursor.Line] = _lines[_cursor.Line] + next;
            _lines.RemoveAt(_cursor.Line + 1);
        }

        _lastEditKind = EditKind.DeleteForward;
        IsModified = true;
    }

    public bool Undo()
    {
        if (_undo.Count == 0)
            return false;

        var snapshot = _undo.Pop();
        _redo.Push(CaptureSnapshot());
        ApplySnapshot(snapshot);
        _lastEditKind = EditKind.None;
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0)
            return false;

        var snapshot = _redo.Pop();
        _undo.Push(CaptureSnapshot());
        ApplySnapshot(snapshot);
        _lastEditKind = EditKind.None;
        return true;
    }

    private void InsertCharRaw(char ch)
    {
        var line = _lines[_cursor.Line];
        _lines[_cursor.Line] = line.Insert(_cursor.Column, ch.ToString());
        _cursor = _cursor with { Column = _cursor.Column + 1 };
    }

    private void InsertNewlineRaw()
    {
        var line = _lines[_cursor.Line];
        var head = line[.._cursor.Column];
        var tail = line[_cursor.Column..];
        _lines[_cursor.Line] = head;
        _lines.Insert(_cursor.Line + 1, tail);
        _cursor = new TextLocation(_cursor.Line + 1, 0);
    }

    private TextLocation ClampLocation(TextLocation location)
    {
        var line = Math.Clamp(location.Line, 0, _lines.Count - 1);
        var col = Math.Clamp(location.Column, 0, _lines[line].Length);
        return new TextLocation(line, col);
    }

    private static int Compare(TextLocation a, TextLocation b)
    {
        if (a.Line != b.Line) return a.Line.CompareTo(b.Line);
        return a.Column.CompareTo(b.Column);
    }

    private string ExtractRange(TextLocation start, TextLocation end)
    {
        if (start.Line == end.Line)
            return _lines[start.Line][start.Column..end.Column];

        var sb = new StringBuilder();
        sb.Append(_lines[start.Line][start.Column..]);
        sb.Append('\n');
        for (var i = start.Line + 1; i < end.Line; i++)
        {
            sb.Append(_lines[i]);
            sb.Append('\n');
        }
        sb.Append(_lines[end.Line][..end.Column]);
        return sb.ToString();
    }

    private Snapshot CaptureSnapshot() => new(_lines.ToArray(), _cursor, IsModified);

    private void ApplySnapshot(Snapshot snapshot)
    {
        _lines.Clear();
        foreach (var line in snapshot.Lines)
            _lines.Add(line);
        if (_lines.Count == 0)
            _lines.Add(string.Empty);
        _cursor = ClampLocation(snapshot.Cursor);
        IsModified = snapshot.IsModified;
    }

    private void PushUndo()
    {
        _undo.Push(CaptureSnapshot());
        _redo.Clear();
        if (_undo.Count > MaxHistoryDepth)
        {
            // Bounded history: drop the oldest entry. Stack doesn't expose this directly,
            // so rebuild the bottom-trimmed stack via array copy.
            var keep = _undo.ToArray();
            _undo.Clear();
            for (var i = MaxHistoryDepth - 1; i >= 0; i--)
                _undo.Push(keep[i]);
        }
    }

    private enum EditKind { None, InsertChar, DeleteBack, DeleteForward }

    private readonly record struct Snapshot(string[] Lines, TextLocation Cursor, bool IsModified);
}
