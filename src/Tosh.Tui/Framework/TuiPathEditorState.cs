namespace Tosh.Tui;

public enum TuiPathEditorActionKind
{
    None,
    TextChanged,
    SubmitText,
    Cancel,
    BrowseRequested,
    PickedPath,
    PickerClosed,
}

public readonly record struct TuiPathEditorAction(
    TuiPathEditorActionKind Kind,
    string? Text = null,
    string? Path = null);

public sealed class TuiPathEditorState
{
    private readonly TuiTextInputState _textInput = new();
    private readonly TuiFilePickerState _picker = new();

    public bool IsBrowsing => _picker.IsOpen;

    /// <summary>
    /// The key that opens the browser instead of being typed. <c>b</c> by default.
    /// </summary>
    /// <remarks>
    /// `TUI-0002`. It is checked before the text input, so whatever it is set to cannot be
    /// typed into the path. The config browser offers <c>b</c> and a separate raw-text editor
    /// for the paths that contain one, which is a fair trade there. A widget standing alone
    /// has no such second editor, so it picks a key that is not a character.
    /// </remarks>
    public ConsoleKey BrowseKey { get; set; } = ConsoleKey.B;

    public string Text => _textInput.Text;

    public void Open(string initialText)
    {
        _textInput.SetText(initialText);
        _picker.Close();
    }

    public void Close()
    {
        _picker.Close();
        _textInput.SetText(string.Empty);
    }

    public void SetText(string text)
    {
        _textInput.SetText(text);
    }

    public string RenderInputWithCursor() => _textInput.RenderWithCursor();

    public void OpenPicker(string startDirectory, TuiFilePickerSelectionMode selectionMode, string? initialSelectionPath, int pageSize)
    {
        _picker.Open(startDirectory, selectionMode, initialSelectionPath, pageSize);
    }

    public IReadOnlyList<string> BuildPickerEntries(int width, int height) => _picker.BuildEntries(width, height);

    public TuiPathEditorAction HandleKey(ConsoleKeyInfo key, int pageSize)
    {
        if (_picker.IsOpen)
        {
            var pickerResult = _picker.HandleKey(key, pageSize);

            return pickerResult.Kind switch
            {
                TuiFilePickerResultKind.Selected => ClosePickerAndCreate(TuiPathEditorActionKind.PickedPath, path: pickerResult.Path),
                TuiFilePickerResultKind.Cancelled => ClosePickerAndCreate(TuiPathEditorActionKind.PickerClosed),
                _ => new TuiPathEditorAction(TuiPathEditorActionKind.None),
            };
        }

        if (key.Key == BrowseKey && !key.Modifiers.HasFlag(ConsoleModifiers.Control) && !key.Modifiers.HasFlag(ConsoleModifiers.Alt))
        {
            return new TuiPathEditorAction(TuiPathEditorActionKind.BrowseRequested);
        }

        var result = _textInput.HandleKey(key);

        return result switch
        {
            TuiTextInputResult.Submit => new TuiPathEditorAction(TuiPathEditorActionKind.SubmitText, Text: _textInput.Text),
            TuiTextInputResult.Cancel => new TuiPathEditorAction(TuiPathEditorActionKind.Cancel),
            TuiTextInputResult.Changed => new TuiPathEditorAction(TuiPathEditorActionKind.TextChanged, Text: _textInput.Text),
            _ => new TuiPathEditorAction(TuiPathEditorActionKind.None),
        };
    }

    private TuiPathEditorAction ClosePickerAndCreate(TuiPathEditorActionKind kind, string? path = null)
    {
        _picker.Close();
        return new TuiPathEditorAction(kind, Path: path);
    }
}
