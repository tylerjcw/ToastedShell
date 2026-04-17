using Tosh.Cli;

namespace Tosh.Cli.Tui;

internal enum TuiTextInputResult
{
    None,
    Changed,
    Submit,
    Cancel,
}

internal sealed class TuiTextInputState
{
    private readonly LineEditorBuffer _buffer = new();

    public string Text => _buffer.Text;

    public int CursorIndex => _buffer.CursorIndex;

    public void SetCursorIndex(int index) => _buffer.SetCursor(index);

    public void SetText(string? text)
    {
        _buffer.SetText(text ?? string.Empty);
    }

    public TuiTextInputResult HandleKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Enter:
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

    public string RenderWithCursor()
    {
        var text = Text;
        var cursor = Math.Clamp(CursorIndex, 0, text.Length);
        return text.Insert(cursor, "|");
    }
}
