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
    // LinkedList used as a double-ended deque: Last = top (most recent),
    // First = bottom (oldest). AddLast/RemoveLast are O(1) push/pop;
    // RemoveFirst is O(1) trim — no array rebuild when the history cap is hit.
    private readonly LinkedList<UndoFrame> _undo = new();
    private readonly LinkedList<UndoFrame> _redo = new();

    private TextLocation _cursor;
    private TextLocation? _selectionAnchor;
    private EditKind _lastEditKind = EditKind.None;

    /// <summary>
    /// Additional carets. Each entry is (cursor, anchor); a null anchor means
    /// the caret has no selection. The "primary" caret is the one stored in
    /// <see cref="_cursor"/>/<see cref="_selectionAnchor"/>; extras live here.
    /// </summary>
    private readonly List<(TextLocation cursor, TextLocation? anchor)> _extraCarets = new();

    /// <summary>
    /// When true, <see cref="PushUndo"/> is a no-op. Used by
    /// <see cref="ApplyAtAllCarets"/> to coalesce a multi-caret edit into a
    /// single undo entry.
    /// </summary>
    private bool _inCompoundEdit;

    public TextBuffer() { }

    public TextBuffer(string initialText)
    {
        LoadText(initialText);
    }

    public int LineCount => _lines.Count;

    public TextLocation Cursor => _cursor;

    public bool IsModified { get; private set; }

    /// <summary>
    /// Monotonic counter bumped on every mutation (insert, delete, undo,
    /// redo, LoadText). External consumers can cache derived data keyed by
    /// this value and invalidate when it changes.
    /// </summary>
    public int Revision { get; private set; }

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

    // ─── Multi-cursor ───────────────────────────────────────────────────
    //
    // The buffer carries one "primary" caret (the legacy <see cref="Cursor"/>)
    // plus any number of additional carets. Edits to the buffer normally
    // affect only the primary; call <see cref="ApplyAtAllCarets"/> to fan
    // an edit out across every caret in a single undo transaction.

    /// <summary>Number of extra carets beyond the primary.</summary>
    public int ExtraCaretCount => _extraCarets.Count;

    /// <summary>True when there is more than one active caret.</summary>
    public bool HasMultipleCarets => _extraCarets.Count > 0;

    /// <summary>
    /// Every caret position (primary + extras), sorted top-to-bottom,
    /// left-to-right. Useful for rendering and for fan-out helpers.
    /// </summary>
    public IReadOnlyList<TextLocation> AllCarets
    {
        get
        {
            var list = new List<TextLocation>(1 + _extraCarets.Count) { _cursor };
            foreach (var (c, _) in _extraCarets) list.Add(c);
            list.Sort(Compare);
            return list;
        }
    }

    /// <summary>
    /// Add an extra caret at <paramref name="cursor"/>. Optional
    /// <paramref name="anchor"/> seeds a selection at that caret. No-op if
    /// the target coincides with the primary or an existing extra.
    /// </summary>
    public void AddCaret(TextLocation cursor, TextLocation? anchor = null)
    {
        cursor = ClampLocation(cursor);
        if (cursor == _cursor) return;
        foreach (var (c, _) in _extraCarets)
            if (c == cursor) return;
        _extraCarets.Add((cursor, anchor is { } a ? ClampLocation(a) : null));
    }

    /// <summary>Drop every extra caret, leaving only the primary.</summary>
    public void ClearExtraCarets() => _extraCarets.Clear();

    /// <summary>
    /// Run <paramref name="action"/> at every caret as a single undo
    /// transaction. Edits are applied bottom-up so an edit at one caret
    /// doesn't shift the positions of carets above it.
    /// </summary>
    public void ApplyAtAllCarets(Action<TextBuffer> action)
    {
        if (_extraCarets.Count == 0) { action(this); return; }

        // Stash every caret + anchor.
        var carets = new List<(TextLocation cursor, TextLocation? anchor)>(1 + _extraCarets.Count)
        {
            (_cursor, _selectionAnchor),
        };
        carets.AddRange(_extraCarets);

        // Bottom-up so edits don't perturb upstream positions.
        carets.Sort((a, b) => Compare(b.cursor, a.cursor));

        PushUndo();
        _inCompoundEdit = true;
        var updated = new List<(TextLocation cursor, TextLocation? anchor)>(carets.Count);
        try
        {
            foreach (var (c, anchor) in carets)
            {
                _cursor = c;
                _selectionAnchor = anchor;
                action(this);
                updated.Add((_cursor, _selectionAnchor));
            }
        }
        finally
        {
            _inCompoundEdit = false;
        }

        // Promote the top-most updated caret to primary; rest become extras.
        updated.Sort((a, b) => Compare(a.cursor, b.cursor));
        _cursor = updated[0].cursor;
        _selectionAnchor = updated[0].anchor;
        _extraCarets.Clear();
        for (var i = 1; i < updated.Count; i++)
            _extraCarets.Add(updated[i]);
        _lastEditKind = EditKind.None;
    }

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

    /// <summary>
    /// Indents every line touched by the current selection by <paramref name="spaces"/> spaces.
    /// Preserves the selection spanning the same lines after the operation.
    /// </summary>
    public void IndentLines(int spaces = 4)
    {
        var sel = Selection;
        if (sel is null) return;
        var (start, end) = sel.Value;
        var pad = new string(' ', spaces);
        PushUndo();
        for (var i = start.Line; i <= end.Line; i++)
            _lines[i] = pad + _lines[i];
        // Keep selection covering the same lines: anchor at col 0 of first line,
        // cursor at end of last line.
        _selectionAnchor = new TextLocation(start.Line, 0);
        _cursor = new TextLocation(end.Line, _lines[end.Line].Length);
        _lastEditKind = EditKind.None;
        IsModified = true;
    }

    /// <summary>
    /// Dedents every line touched by the current selection by up to
    /// <paramref name="spaces"/> leading spaces per line.
    /// </summary>
    public void DedentLines(int spaces = 4)
    {
        var sel = Selection;
        if (sel is null) return;
        var (start, end) = sel.Value;
        PushUndo();
        for (var i = start.Line; i <= end.Line; i++)
        {
            var line = _lines[i];
            var removed = 0;
            while (removed < spaces && removed < line.Length && line[removed] == ' ')
                removed++;
            _lines[i] = line[removed..];
        }
        // Same anchor convention as IndentLines.
        _selectionAnchor = new TextLocation(start.Line, 0);
        _cursor = new TextLocation(end.Line, _lines[end.Line].Length);
        _lastEditKind = EditKind.None;
        IsModified = true;
    }

    public string GetLine(int lineIndex) =>
        lineIndex >= 0 && lineIndex < _lines.Count ? _lines[lineIndex] : string.Empty;

    public int GetLineLength(int lineIndex) => GetLine(lineIndex).Length;

    /// <summary>Returns the full document as a single string with '\n' separators.</summary>
    public string GetText() => string.Join('\n', _lines);

    /// <summary>Replaces the entire buffer; resets cursor to (0,0) and clears history.</summary>
    public void LoadText(string text)
    {
        Revision++;
        _braceCache = null;
        _undo.Clear();
        _redo.Clear();
        _lines.Clear();
        _extraCarets.Clear();
        _selectionAnchor = null;

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

    /// <summary>
    /// Replace the entire buffer text as a single undoable edit. Unlike
    /// <see cref="LoadText"/>, the existing undo/redo history is preserved
    /// and a new snapshot is pushed so the operation can be undone.
    /// </summary>
    public void ReplaceAll(string text)
    {
        var normalized = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        // Skip the work and the undo entry when nothing actually changes.
        if (string.Equals(GetText(), normalized, StringComparison.Ordinal)) return;

        PushUndo();
        _lines.Clear();
        foreach (var part in normalized.Split('\n')) _lines.Add(part);
        if (_lines.Count == 0) _lines.Add(string.Empty);
        _cursor = ClampLocation(_cursor);
        _selectionAnchor = null;
        _extraCarets.Clear();
        _lastEditKind = EditKind.None;
        IsModified = true;
    }

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
        if (ch == '\n') { InsertNewline(); return; }

        // Break coalescing at word/separator boundaries so undo is word-granular.
        var kind = IsWordSeparator(ch) ? EditKind.InsertSepChar : EditKind.InsertWordChar;
        if (_lastEditKind != kind) PushUndo();
        // Always bump revision so gutter/cache consumers see each keystroke.
        MarkMutated();

        var line = _lines[_cursor.Line];
        _lines[_cursor.Line] = line.Insert(_cursor.Column, ch.ToString());
        _cursor = _cursor with { Column = _cursor.Column + 1 };
        _lastEditKind = kind;
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
        if (_cursor.Line == 0 && _cursor.Column == 0) return;

        if (_lastEditKind != EditKind.DeleteBack) PushUndo();
        MarkMutated();

        if (_cursor.Column > 0)
        {
            var line = _lines[_cursor.Line];
            _lines[_cursor.Line] = line.Remove(_cursor.Column - 1, 1);
            _cursor = _cursor with { Column = _cursor.Column - 1 };
        }
        else
        {
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
        if (_cursor.Column == lineLen && _cursor.Line == _lines.Count - 1) return;

        if (_lastEditKind != EditKind.DeleteForward) PushUndo();
        MarkMutated();

        if (_cursor.Column < lineLen)
        {
            var line = _lines[_cursor.Line];
            _lines[_cursor.Line] = line.Remove(_cursor.Column, 1);
        }
        else
        {
            var next = _lines[_cursor.Line + 1];
            _lines[_cursor.Line] = _lines[_cursor.Line] + next;
            _lines.RemoveAt(_cursor.Line + 1);
        }

        _lastEditKind = EditKind.DeleteForward;
        IsModified = true;
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var snapshot = _undo.Last!.Value;
        _undo.RemoveLast();
        _redo.AddLast(CaptureSnapshot());
        ApplySnapshot(snapshot);
        _lastEditKind = EditKind.None;
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        var snapshot = _redo.Last!.Value;
        _redo.RemoveLast();
        _undo.AddLast(CaptureSnapshot());
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

    private UndoFrame CaptureSnapshot() => new(_lines.ToArray(), _cursor.Line, _cursor.Column, IsModified);

    private void ApplySnapshot(UndoFrame snapshot)
    {
        Revision++;
        _braceCache = null;
        _lines.Clear();
        foreach (var line in snapshot.Lines)
            _lines.Add(line);
        if (_lines.Count == 0)
            _lines.Add(string.Empty);
        _cursor = ClampLocation(new TextLocation(snapshot.CursorLine, snapshot.CursorColumn));
        _selectionAnchor = null;
        _extraCarets.Clear();
        IsModified = snapshot.IsModified;
    }

    /// <summary>Undo stack snapshot, bottom (oldest) → top (most recent).</summary>
    // LinkedList enumerates First→Last which is already oldest→newest.
    public IReadOnlyList<UndoFrame> ExportUndoStack() => _undo.ToArray();

    /// <summary>Redo stack snapshot, bottom (oldest) → top (most recent).</summary>
    public IReadOnlyList<UndoFrame> ExportRedoStack() => _redo.ToArray();

    /// <summary>
    /// Replace the undo/redo stacks with the supplied frames (bottom → top).
    /// Does not touch the live buffer content or cursor.
    /// </summary>
    public void ImportHistory(IReadOnlyList<UndoFrame> undo, IReadOnlyList<UndoFrame> redo)
    {
        _undo.Clear();
        _redo.Clear();
        // Input is oldest→newest; AddLast preserves that order: oldest at First, newest at Last.
        foreach (var f in undo) _undo.AddLast(f);
        foreach (var f in redo) _redo.AddLast(f);
        _lastEditKind = EditKind.None;
    }

    // Called on every content mutation, even coalesced ones, so that Revision
    // and the brace cache stay fresh for every rendered frame.
    private void MarkMutated()
    {
        Revision++;
        _braceCache = null;
    }

    private void PushUndo()
    {
        if (_inCompoundEdit) return;
        MarkMutated();
        _undo.AddLast(CaptureSnapshot());
        _redo.Clear();
        // O(1) trim: drop the oldest entry from the front of the list.
        if (_undo.Count > MaxHistoryDepth)
            _undo.RemoveFirst();
    }

    // Word chars (letters/digits/_) coalesce together; separator chars coalesce
    // together; but the two groups never coalesce with each other. This gives
    // VS Code-style word-granular undo: each distinct word or run of punctuation
    // becomes its own undo step.
    private enum EditKind { None, InsertWordChar, InsertSepChar, DeleteBack, DeleteForward }

    // ─── Brace-depth cache (used by gutter rendering) ────────────────

    private BraceLineInfo[]? _braceCache;

    /// <summary>
    /// Per-line brace nesting depth and opener/closer flags, computed
    /// once per <see cref="Revision"/>. Treats <c>#</c> as a line
    /// comment and respects single/double-quoted strings with backslash
    /// escapes — matches the .tosh grammar's bracketing rules.
    /// </summary>
    public IReadOnlyList<BraceLineInfo> GetBraceLineInfo()
    {
        if (_braceCache is not null) return _braceCache;

        var info = new BraceLineInfo[_lines.Count];
        var depth = 0;

        for (var i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];
            var startsWithCloser = LineStartsWithCloser(line);
            var endsWithOpener = LineEndsWithOpener(line);
            var prevEndsWithOpener = i > 0 && info[i - 1].EndsWithOpener;

            var effective = Math.Max(0, depth - (startsWithCloser ? 1 : 0));
            if (prevEndsWithOpener && !startsWithCloser)
                effective = Math.Max(1, effective);

            info[i] = new BraceLineInfo(effective, startsWithCloser, endsWithOpener);
            depth = Math.Max(0, depth + ComputeBraceDelta(line));
        }

        _braceCache = info;
        return info;
    }

    private static bool LineStartsWithCloser(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.Length > 0 && trimmed[0] == '}';
    }

    private static bool LineEndsWithOpener(string line) =>
        line.TrimEnd().EndsWith('{');

    private static int ComputeBraceDelta(string line)
    {
        var delta = 0;
        var inSingle = false;
        var inDouble = false;
        var escaping = false;
        var inComment = false;

        foreach (var ch in line)
        {
            if (inComment) continue;
            if (inSingle)
            {
                if (escaping) { escaping = false; continue; }
                if (ch == '\\') { escaping = true; continue; }
                if (ch == '\'') inSingle = false;
                continue;
            }
            if (inDouble)
            {
                if (escaping) { escaping = false; continue; }
                if (ch == '\\') { escaping = true; continue; }
                if (ch == '"') inDouble = false;
                continue;
            }
            switch (ch)
            {
                case '#': inComment = true; break;
                case '\'': inSingle = true; break;
                case '"': inDouble = true; break;
                case '{': delta++; break;
                case '}': delta--; break;
            }
        }
        return delta;
    }
}

/// <summary>
/// Per-line brace info exposed by <see cref="TextBuffer.GetBraceLineInfo"/>.
/// <see cref="Depth"/> is the visual nesting level used by the gutter
/// renderer (bars to draw); <see cref="StartsWithCloser"/> and
/// <see cref="EndsWithOpener"/> drive the open/close/transition glyphs.
/// </summary>
public readonly record struct BraceLineInfo(int Depth, bool StartsWithCloser, bool EndsWithOpener);

/// <summary>
/// One persisted undo/redo frame: full line array plus cursor + dirty flag.
/// </summary>
public readonly record struct UndoFrame(string[] Lines, int CursorLine, int CursorColumn, bool IsModified);
