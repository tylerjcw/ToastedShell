namespace Tosh.Cli.Tui;

internal enum TuiOptionPickerActionKind
{
    None,
    Commit,
    Cancel,
    SelectionUnavailable,
}

internal readonly record struct TuiOptionPickerAction<TItem>(
    TuiOptionPickerActionKind Kind,
    TItem? Item = default,
    string? Key = null);

internal sealed class TuiOptionPickerState<TItem>
{
    private readonly TuiListState<TItem> _items = new();
    private Func<TItem, string>? _keySelector;

    public IReadOnlyList<TItem> Items => _items.Items;

    public int SelectedIndex => _items.SelectedIndex;

    public string? SelectedKey { get; private set; }

    public void Open(
        IReadOnlyList<TItem> items,
        int pageSize,
        Func<TItem, string> keySelector,
        string? preferredKey = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(keySelector);

        _keySelector = keySelector;
        Refresh(items, pageSize, preferredKey);
    }

    public void Close()
    {
        SelectedKey = null;
        _items.SetItems(Array.Empty<TItem>(), 1);
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

        UpdateSelectedKey();
    }

    public bool TryGetSelected(out TItem item) => _items.TryGetSelected(out item);

    public TuiOptionPickerAction<TItem> HandleKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                _items.MovePrevious();
                UpdateSelectedKey();
                return new TuiOptionPickerAction<TItem>(TuiOptionPickerActionKind.None);
            case ConsoleKey.DownArrow:
                _items.MoveNext();
                UpdateSelectedKey();
                return new TuiOptionPickerAction<TItem>(TuiOptionPickerActionKind.None);
            case ConsoleKey.PageUp:
                _items.PageUp();
                UpdateSelectedKey();
                return new TuiOptionPickerAction<TItem>(TuiOptionPickerActionKind.None);
            case ConsoleKey.PageDown:
                _items.PageDown();
                UpdateSelectedKey();
                return new TuiOptionPickerAction<TItem>(TuiOptionPickerActionKind.None);
            case ConsoleKey.Home:
                _items.Home();
                UpdateSelectedKey();
                return new TuiOptionPickerAction<TItem>(TuiOptionPickerActionKind.None);
            case ConsoleKey.End:
                _items.End();
                UpdateSelectedKey();
                return new TuiOptionPickerAction<TItem>(TuiOptionPickerActionKind.None);
            case ConsoleKey.Enter:
                return CreateSelectionAction(TuiOptionPickerActionKind.Commit);
            case ConsoleKey.Escape:
                return new TuiOptionPickerAction<TItem>(TuiOptionPickerActionKind.Cancel);
            default:
                return new TuiOptionPickerAction<TItem>(TuiOptionPickerActionKind.None);
        }
    }

    private TuiOptionPickerAction<TItem> CreateSelectionAction(TuiOptionPickerActionKind kind)
    {
        if (!_items.TryGetSelected(out var selected) || _keySelector is null)
        {
            return new TuiOptionPickerAction<TItem>(TuiOptionPickerActionKind.SelectionUnavailable);
        }

        return new TuiOptionPickerAction<TItem>(kind, selected, _keySelector(selected));
    }

    private void UpdateSelectedKey()
    {
        if (_keySelector is null || !_items.TryGetSelected(out var selected))
        {
            SelectedKey = null;
            return;
        }

        SelectedKey = _keySelector(selected);
    }
}
