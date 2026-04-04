namespace Tosh.Cli.Tui;

internal enum TuiCollectionEditorInputMode
{
    None,
    AddItem,
    EditItem,
}

internal enum TuiCollectionEditorActionKind
{
    None,
    SubmitInput,
    RemoveItem,
    Apply,
    Save,
    Close,
    InputCancelled,
    EditUnavailable,
    RemoveUnavailable,
}

internal readonly record struct TuiCollectionEditorAction<TItem>(
    TuiCollectionEditorActionKind Kind,
    TItem? Item = default,
    string? Key = null,
    string? Text = null,
    TuiCollectionEditorInputMode InputMode = TuiCollectionEditorInputMode.None);

internal readonly record struct TuiCollectionEditorVisibleItem<TItem>(
    TItem Item,
    int Index,
    bool IsSelected);

internal sealed class TuiCollectionEditorState<TItem>
{
    private readonly TuiListState<TItem> _items = new();
    private readonly TuiTextInputState _textInput = new();
    private Func<TItem, string>? _keySelector;
    private Func<TItem, string>? _editValueSelector;

    public TuiCollectionEditorInputMode InputMode { get; private set; }

    public string? EditingItemKey { get; private set; }

    public IReadOnlyList<TItem> Items => _items.Items;

    public void Open(
        IReadOnlyList<TItem> items,
        int pageSize,
        Func<TItem, string> keySelector,
        Func<TItem, string> editValueSelector,
        string? preferredKey = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(keySelector);
        ArgumentNullException.ThrowIfNull(editValueSelector);

        _keySelector = keySelector;
        _editValueSelector = editValueSelector;
        InputMode = TuiCollectionEditorInputMode.None;
        EditingItemKey = null;
        Refresh(items, pageSize, preferredKey);
    }

    public void Close()
    {
        InputMode = TuiCollectionEditorInputMode.None;
        EditingItemKey = null;
        _items.SetItems(Array.Empty<TItem>(), 1);
        _textInput.SetText(string.Empty);
    }

    public void Refresh(IReadOnlyList<TItem> items, int pageSize, string? preferredKey = null)
    {
        ArgumentNullException.ThrowIfNull(items);

        var selectedKey = preferredKey;

        if (selectedKey is null && _keySelector is not null && _items.TryGetSelected(out var selected))
        {
            selectedKey = _keySelector(selected);
        }

        _items.SetItems(items, Math.Max(1, pageSize));

        if (_keySelector is not null && selectedKey is not null)
        {
            var selectedIndex = items
                .Select((item, index) => new { item, index })
                .FirstOrDefault(entry => string.Equals(_keySelector(entry.item), selectedKey, StringComparison.OrdinalIgnoreCase))
                ?.index ?? -1;

            if (selectedIndex >= 0)
            {
                _items.SelectIndex(selectedIndex);
            }
        }

        if (InputMode == TuiCollectionEditorInputMode.None && _keySelector is not null && _items.TryGetSelected(out var current))
        {
            EditingItemKey = _keySelector(current);
        }
    }

    public bool TryGetSelected(out TItem item) => _items.TryGetSelected(out item);

    public IReadOnlyList<TuiCollectionEditorVisibleItem<TItem>> GetVisibleItems()
    {
        var range = _items.Scroll.GetVisibleRange();
        var visibleItems = range.Length == 0
            ? _items.Items.Select((item, index) => new TuiCollectionEditorVisibleItem<TItem>(item, index, index == _items.SelectedIndex))
            : _items.Items
                .Skip(range.Start)
                .Take(range.Length)
                .Select((item, offset) =>
                {
                    var index = range.Start + offset;
                    return new TuiCollectionEditorVisibleItem<TItem>(item, index, index == _items.SelectedIndex);
                });

        return visibleItems.ToArray();
    }

    public string RenderInputWithCursor() => _textInput.RenderWithCursor();

    public TuiCollectionEditorAction<TItem> HandleKey(ConsoleKeyInfo key)
    {
        if (InputMode != TuiCollectionEditorInputMode.None)
        {
            var inputResult = _textInput.HandleKey(key);

            return inputResult switch
            {
                TuiTextInputResult.Submit => new TuiCollectionEditorAction<TItem>(
                    TuiCollectionEditorActionKind.SubmitInput,
                    Key: EditingItemKey,
                    Text: _textInput.Text,
                    InputMode: InputMode),
                TuiTextInputResult.Cancel => new TuiCollectionEditorAction<TItem>(
                    TuiCollectionEditorActionKind.InputCancelled,
                    Key: EditingItemKey,
                    InputMode: InputMode),
                _ => new TuiCollectionEditorAction<TItem>(TuiCollectionEditorActionKind.None),
            };
        }

        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                _items.MovePrevious();
                UpdateSelectedKey();
                return new TuiCollectionEditorAction<TItem>(TuiCollectionEditorActionKind.None);
            case ConsoleKey.DownArrow:
                _items.MoveNext();
                UpdateSelectedKey();
                return new TuiCollectionEditorAction<TItem>(TuiCollectionEditorActionKind.None);
            case ConsoleKey.PageUp:
                _items.PageUp();
                UpdateSelectedKey();
                return new TuiCollectionEditorAction<TItem>(TuiCollectionEditorActionKind.None);
            case ConsoleKey.PageDown:
                _items.PageDown();
                UpdateSelectedKey();
                return new TuiCollectionEditorAction<TItem>(TuiCollectionEditorActionKind.None);
            case ConsoleKey.Home:
                _items.Home();
                UpdateSelectedKey();
                return new TuiCollectionEditorAction<TItem>(TuiCollectionEditorActionKind.None);
            case ConsoleKey.End:
                _items.End();
                UpdateSelectedKey();
                return new TuiCollectionEditorAction<TItem>(TuiCollectionEditorActionKind.None);
            case ConsoleKey.N:
                BeginAdd();
                return new TuiCollectionEditorAction<TItem>(TuiCollectionEditorActionKind.None);
            case ConsoleKey.Enter:
            case ConsoleKey.E:
                return BeginEditSelected();
            case ConsoleKey.Delete:
            case ConsoleKey.R:
                if (!_items.TryGetSelected(out var selectedForRemoval) || _keySelector is null)
                {
                    return new TuiCollectionEditorAction<TItem>(TuiCollectionEditorActionKind.RemoveUnavailable);
                }

                return new TuiCollectionEditorAction<TItem>(
                    TuiCollectionEditorActionKind.RemoveItem,
                    selectedForRemoval,
                    _keySelector(selectedForRemoval));
            case ConsoleKey.A when !key.Modifiers.HasFlag(ConsoleModifiers.Shift):
                return new TuiCollectionEditorAction<TItem>(TuiCollectionEditorActionKind.Apply);
            case ConsoleKey.S:
                return new TuiCollectionEditorAction<TItem>(TuiCollectionEditorActionKind.Save);
            case ConsoleKey.Escape:
                return new TuiCollectionEditorAction<TItem>(TuiCollectionEditorActionKind.Close);
            default:
                return new TuiCollectionEditorAction<TItem>(TuiCollectionEditorActionKind.None);
        }
    }

    public void CompleteInput(string? preferredKey = null)
    {
        InputMode = TuiCollectionEditorInputMode.None;
        EditingItemKey = preferredKey;
    }

    public void CancelInput()
    {
        InputMode = TuiCollectionEditorInputMode.None;
    }

    private void BeginAdd()
    {
        InputMode = TuiCollectionEditorInputMode.AddItem;
        EditingItemKey = null;
        _textInput.SetText(string.Empty);
    }

    private TuiCollectionEditorAction<TItem> BeginEditSelected()
    {
        if (!_items.TryGetSelected(out var selected) || _keySelector is null || _editValueSelector is null)
        {
            return new TuiCollectionEditorAction<TItem>(TuiCollectionEditorActionKind.EditUnavailable);
        }

        InputMode = TuiCollectionEditorInputMode.EditItem;
        EditingItemKey = _keySelector(selected);
        _textInput.SetText(_editValueSelector(selected));
        return new TuiCollectionEditorAction<TItem>(TuiCollectionEditorActionKind.None);
    }

    private void UpdateSelectedKey()
    {
        if (_keySelector is null || !_items.TryGetSelected(out var selected))
        {
            return;
        }

        EditingItemKey = _keySelector(selected);
    }
}
