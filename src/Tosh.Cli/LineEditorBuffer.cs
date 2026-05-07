using System.Text;

namespace Tosh.Cli;

public sealed class LineEditorBuffer
{
    private readonly StringBuilder _buffer = new();
    private readonly Stack<EditorSnapshot> _undoStack = new();
    private readonly Stack<EditorSnapshot> _redoStack = new();
    private const int MaxHistoryDepth = 256;
    private EditKind _lastEditKind = EditKind.None;

    private enum EditKind { None, InsertChar, DeleteBack, DeleteForward }

    public LineEditorBuffer(string text = "")
    {
        _buffer.Append(text ?? string.Empty);
        CursorIndex = _buffer.Length;
    }

    public int CursorIndex { get; private set; }

    public string Text => _buffer.ToString();

    public bool CanUndo => _undoStack.Count > 0;

    public bool CanRedo => _redoStack.Count > 0;

    /// <summary>
    /// Ends the current coalescing run so the next character-level edit opens a fresh undo entry.
    /// Call after any cursor movement or structural edit.
    /// </summary>
    public void BreakUndoCoalescing() => _lastEditKind = EditKind.None;

    public void SetText(string? text)
    {
        var newText = text ?? string.Empty;

        if (string.Equals(_buffer.ToString(), newText, StringComparison.Ordinal))
        {
            return;
        }

        PushUndoSnapshot();
        _buffer.Clear();
        _buffer.Append(newText);
        CursorIndex = _buffer.Length;
        _lastEditKind = EditKind.None;
    }

    public void SetCursor(int cursorIndex)
    {
        _lastEditKind = EditKind.None;
        CursorIndex = Math.Clamp(cursorIndex, 0, _buffer.Length);
    }

    public bool ReplaceRange(int start, int length, string replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);

        start = Math.Clamp(start, 0, _buffer.Length);
        length = Math.Clamp(length, 0, _buffer.Length - start);

        if (length == 0 && replacement.Length == 0)
        {
            return false;
        }

        PushUndoSnapshot();

        _buffer.Remove(start, length);
        _buffer.Insert(start, replacement);
        CursorIndex = start + replacement.Length;
        _lastEditKind = EditKind.None;
        return true;
    }

    public bool Insert(char value)
    {
        if (_lastEditKind != EditKind.InsertChar)
        {
            PushUndoSnapshot();
        }
        _buffer.Insert(CursorIndex, value);
        CursorIndex++;
        _lastEditKind = EditKind.InsertChar;
        return true;
    }

    public bool Insert(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length == 0)
        {
            return false;
        }

        PushUndoSnapshot();
        _buffer.Insert(CursorIndex, value);
        CursorIndex += value.Length;
        _lastEditKind = EditKind.None;
        return true;
    }

    public bool Backspace()
    {
        if (CursorIndex == 0)
        {
            return false;
        }

        if (_lastEditKind != EditKind.DeleteBack)
        {
            PushUndoSnapshot();
        }
        _buffer.Remove(CursorIndex - 1, 1);
        CursorIndex--;
        _lastEditKind = EditKind.DeleteBack;
        return true;
    }

    public bool Delete()
    {
        if (CursorIndex >= _buffer.Length)
        {
            return false;
        }

        if (_lastEditKind != EditKind.DeleteForward)
        {
            PushUndoSnapshot();
        }
        _buffer.Remove(CursorIndex, 1);
        _lastEditKind = EditKind.DeleteForward;
        return true;
    }

    /// <summary>
    /// Atomically accepts a completion: sets the buffer to <paramref name="baseText"/> and
    /// applies the replacement, recording the whole operation as a single undo entry.
    /// </summary>
    public void ApplyCompletion(string baseText, int replacementStart, int replacementLength, string replacement)
    {
        PushUndoSnapshot();
        _buffer.Clear();
        _buffer.Append(baseText ?? string.Empty);
        var start = Math.Clamp(replacementStart, 0, _buffer.Length);
        var length = Math.Clamp(replacementLength, 0, _buffer.Length - start);
        _buffer.Remove(start, length);
        _buffer.Insert(start, replacement);
        CursorIndex = start + replacement.Length;
        _lastEditKind = EditKind.None;
    }

    public bool MoveLeft()
    {
        _lastEditKind = EditKind.None;
        if (CursorIndex == 0)
        {
            return false;
        }

        CursorIndex--;
        return true;
    }

    public bool MoveRight()
    {
        _lastEditKind = EditKind.None;
        if (CursorIndex >= _buffer.Length)
        {
            return false;
        }

        CursorIndex++;
        return true;
    }

    public bool MoveWordLeft()
    {
        _lastEditKind = EditKind.None;
        if (CursorIndex == 0)
        {
            return false;
        }

        var position = CursorIndex;

        while (position > 0 && char.IsWhiteSpace(_buffer[position - 1]))
        {
            position--;
        }

        while (position > 0 && !char.IsWhiteSpace(_buffer[position - 1]))
        {
            position--;
        }

        if (position == CursorIndex)
        {
            return false;
        }

        CursorIndex = position;
        return true;
    }

    public bool MoveWordRight()
    {
        _lastEditKind = EditKind.None;
        if (CursorIndex >= _buffer.Length)
        {
            return false;
        }

        var position = CursorIndex;

        while (position < _buffer.Length && char.IsWhiteSpace(_buffer[position]))
        {
            position++;
        }

        while (position < _buffer.Length && !char.IsWhiteSpace(_buffer[position]))
        {
            position++;
        }

        if (position == CursorIndex)
        {
            return false;
        }

        CursorIndex = position;
        return true;
    }

    public bool MoveHome()
    {
        _lastEditKind = EditKind.None;
        if (CursorIndex == 0)
        {
            return false;
        }

        CursorIndex = 0;
        return true;
    }

    public bool MoveEnd()
    {
        _lastEditKind = EditKind.None;
        if (CursorIndex == _buffer.Length)
        {
            return false;
        }

        CursorIndex = _buffer.Length;
        return true;
    }

    public bool Clear()
    {
        if (_buffer.Length == 0 && CursorIndex == 0)
        {
            return false;
        }

        PushUndoSnapshot();
        _buffer.Clear();
        CursorIndex = 0;
        _lastEditKind = EditKind.None;
        return true;
    }

    public bool DeleteWordBackward()
    {
        if (CursorIndex == 0)
        {
            return false;
        }

        var end = CursorIndex;
        var pos = end;

        while (pos > 0 && char.IsWhiteSpace(_buffer[pos - 1]))
        {
            pos--;
        }

        while (pos > 0 && !char.IsWhiteSpace(_buffer[pos - 1]))
        {
            pos--;
        }

        PushUndoSnapshot();
        _buffer.Remove(pos, end - pos);
        CursorIndex = pos;
        _lastEditKind = EditKind.None;
        return true;
    }

    public bool KillToEnd()
    {
        if (CursorIndex >= _buffer.Length)
        {
            return false;
        }

        PushUndoSnapshot();
        _buffer.Remove(CursorIndex, _buffer.Length - CursorIndex);
        _lastEditKind = EditKind.None;
        return true;
    }

    public bool Undo()
    {
        if (_undoStack.Count == 0)
        {
            return false;
        }

        PushRedoSnapshot();
        ApplySnapshot(_undoStack.Pop());
        _lastEditKind = EditKind.None;
        return true;
    }

    public bool Redo()
    {
        if (_redoStack.Count == 0)
        {
            return false;
        }

        PushUndoSnapshot(clearRedo: false);
        ApplySnapshot(_redoStack.Pop());
        _lastEditKind = EditKind.None;
        return true;
    }

    private void PushUndoSnapshot(bool clearRedo = true)
    {
        PushSnapshot(_undoStack, new EditorSnapshot(_buffer.ToString(), CursorIndex));

        if (clearRedo)
        {
            _redoStack.Clear();
        }
    }

    private void PushRedoSnapshot()
    {
        PushSnapshot(_redoStack, new EditorSnapshot(_buffer.ToString(), CursorIndex));
    }

    private static void PushSnapshot(Stack<EditorSnapshot> stack, EditorSnapshot snapshot)
    {
        if (stack.Count >= MaxHistoryDepth)
        {
            var keep = stack.Reverse().Take(MaxHistoryDepth - 1).Reverse().ToArray();
            stack.Clear();

            foreach (var item in keep)
            {
                stack.Push(item);
            }
        }

        stack.Push(snapshot);
    }

    private void ApplySnapshot(EditorSnapshot snapshot)
    {
        _buffer.Clear();
        _buffer.Append(snapshot.Text);
        CursorIndex = Math.Clamp(snapshot.CursorIndex, 0, _buffer.Length);
    }

    private readonly record struct EditorSnapshot(string Text, int CursorIndex);
}
