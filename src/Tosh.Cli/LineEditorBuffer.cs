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

    public bool Insert(char value)
    {
        _buffer.Insert(CursorIndex, value);
        CursorIndex++;
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
