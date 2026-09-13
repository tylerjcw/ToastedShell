using Tosh.Tui.Editing;

namespace Tosh.Tui;

public enum TuiTextInputResult
{
    None,
    Changed,
    Submit,
    Cancel,
}

public sealed class TuiTextInputState
{
    private readonly LineEditorBuffer _buffer = new();

    public string Text => _buffer.Text;

    public int CursorIndex => _buffer.CursorIndex;

    public void SetCursorIndex(int index) => _buffer.SetCursor(index);

    public void SetText(string? text)
    {
        _buffer.SetText(text ?? string.Empty);
    }

    public TuiTextInputResult HandleKey(ConsoleKeyInfo key) => HandleKey(key, multiline: false);

    /// <summary>
    /// Handles one key. In <paramref name="multiline"/> mode Enter inserts a newline and
    /// Ctrl+Enter submits, so a caller that asked for multiline can actually type a second
    /// line; single-line mode keeps Enter as submit.
    /// </summary>
    public TuiTextInputResult HandleKey(ConsoleKeyInfo key, bool multiline)
    {
        switch (key.Key)
        {
            case ConsoleKey.Enter:
                if (multiline && (key.Modifiers & ConsoleModifiers.Control) == 0)
                {
                    _buffer.Insert('\n');
                    return TuiTextInputResult.Changed;
                }

                return TuiTextInputResult.Submit;
            case ConsoleKey.Escape:
                return TuiTextInputResult.Cancel;
            case ConsoleKey.Backspace:
                return _buffer.Backspace() ? TuiTextInputResult.Changed : TuiTextInputResult.None;
            case ConsoleKey.Delete:
                return _buffer.Delete() ? TuiTextInputResult.Changed : TuiTextInputResult.None;
            case ConsoleKey.LeftArrow:
                return _buffer.MoveLeft() ? TuiTextInputResult.Changed : TuiTextInputResult.None;
            case ConsoleKey.RightArrow:
                return _buffer.MoveRight() ? TuiTextInputResult.Changed : TuiTextInputResult.None;
            case ConsoleKey.Home:
                return _buffer.MoveHome() ? TuiTextInputResult.Changed : TuiTextInputResult.None;
            case ConsoleKey.End:
                return _buffer.MoveEnd() ? TuiTextInputResult.Changed : TuiTextInputResult.None;
        }

        if (!char.IsControl(key.KeyChar))
        {
            _buffer.Insert(key.KeyChar);
            return TuiTextInputResult.Changed;
        }

        return TuiTextInputResult.None;
    }

    public string RenderWithCursor() => RenderWithCursor(mask: false);

    /// <summary>
    /// Renders the text with a cursor marker. When <paramref name="mask"/> is set every
    /// character but a line break is replaced, so a password field does not display what
    /// was typed. Masking happens here rather than at the call site so the cursor stays
    /// on the character it is actually on.
    /// </summary>
    public string RenderWithCursor(bool mask)
    {
        var text = Text;

        if (mask)
        {
            text = string.Create(text.Length, text, static (span, source) =>
            {
                for (var i = 0; i < source.Length; i++)
                {
                    span[i] = source[i] == '\n' ? '\n' : '\u2022';
                }
            });
        }

        var cursor = Math.Clamp(CursorIndex, 0, text.Length);
        return text.Insert(cursor, "|");
    }
}
