using System.Text;

namespace Tosh.Cli;

public sealed class LineEditorBuffer
{
    private readonly StringBuilder _buffer = new();

    public LineEditorBuffer(string text = "")
    {
        SetText(text);
    }

    public int CursorIndex { get; private set; }

    public string Text => _buffer.ToString();

    public void SetText(string? text)
    {
        _buffer.Clear();
        _buffer.Append(text ?? string.Empty);
        CursorIndex = _buffer.Length;
    }

    public void SetCursor(int cursorIndex)
    {
        CursorIndex = Math.Clamp(cursorIndex, 0, _buffer.Length);
    }

    public bool ReplaceRange(int start, int length, string replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);

        start = Math.Clamp(start, 0, _buffer.Length);
        length = Math.Clamp(length, 0, _buffer.Length - start);

        _buffer.Remove(start, length);
        _buffer.Insert(start, replacement);
        CursorIndex = start + replacement.Length;
        return true;
    }

    public bool Insert(char value)
    {
        _buffer.Insert(CursorIndex, value);
        CursorIndex++;
        return true;
    }

    public bool Insert(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _buffer.Insert(CursorIndex, value);
        CursorIndex += value.Length;
        return true;
    }

    public bool Backspace()
    {
        if (CursorIndex == 0)
        {
            return false;
        }

        _buffer.Remove(CursorIndex - 1, 1);
        CursorIndex--;
        return true;
    }

    public bool Delete()
    {
        if (CursorIndex >= _buffer.Length)
        {
            return false;
        }

        _buffer.Remove(CursorIndex, 1);
        return true;
    }

    public bool MoveLeft()
    {
        if (CursorIndex == 0)
        {
            return false;
        }

        CursorIndex--;
        return true;
    }

    public bool MoveRight()
    {
        if (CursorIndex >= _buffer.Length)
        {
            return false;
        }

        CursorIndex++;
        return true;
    }

    public bool MoveWordLeft()
    {
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
        if (CursorIndex == 0)
        {
            return false;
        }

        CursorIndex = 0;
        return true;
    }

    public bool MoveEnd()
    {
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

        _buffer.Clear();
        CursorIndex = 0;
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

        _buffer.Remove(pos, end - pos);
        CursorIndex = pos;
        return true;
    }

    public bool KillToEnd()
    {
        if (CursorIndex >= _buffer.Length)
        {
            return false;
        }

        _buffer.Remove(CursorIndex, _buffer.Length - CursorIndex);
        return true;
    }
}
