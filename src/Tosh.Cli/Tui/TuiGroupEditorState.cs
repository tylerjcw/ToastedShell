namespace Tosh.Cli.Tui;

internal enum TuiGroupEditorActionKind
{
    None,
    ToggleSelected,
    EditSelected,
    RawEditSelected,
    Close,
    SelectionUnavailable,
}

internal readonly record struct TuiGroupEditorAction<TItem>(
    TuiGroupEditorActionKind Kind,
    TItem? Item = default,
    string? Key = null);

internal readonly record struct TuiGroupEditorVisibleItem<TItem>(
    TItem Item,
    int Index,
    bool IsSelected);

internal sealed class TuiGroupEditorState<TItem>
{
    private readonly TuiListState<TItem> _items = new();
    private Func<TItem, string>? _keySelector;

    public string? SelectedKey { get; private set; }

    public IReadOnlyList<TItem> Items => _items.Items;

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

    public IReadOnlyList<TuiGroupEditorVisibleItem<TItem>> GetVisibleItems()
    {
        var range = _items.Scroll.GetVisibleRange();
        var visibleItems = range.Length == 0
            ? _items.Items.Select((item, index) => new TuiGroupEditorVisibleItem<TItem>(item, index, index == _items.SelectedIndex))
            : _items.Items
                .Skip(range.Start)
                .Take(range.Length)
                .Select((item, offset) =>
                {
                    var index = range.Start + offset;
                    return new TuiGroupEditorVisibleItem<TItem>(item, index, index == _items.SelectedIndex);
                });

        return visibleItems.ToArray();
    }

    public TuiGroupEditorAction<TItem> HandleKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                _items.MovePrevious();
                UpdateSelectedKey();
                return new TuiGroupEditorAction<TItem>(TuiGroupEditorActionKind.None);
            case ConsoleKey.DownArrow:
                _items.MoveNext();
                UpdateSelectedKey();
                return new TuiGroupEditorAction<TItem>(TuiGroupEditorActionKind.None);
            case ConsoleKey.Home:
                _items.Home();
                UpdateSelectedKey();
                return new TuiGroupEditorAction<TItem>(TuiGroupEditorActionKind.None);
            case ConsoleKey.End:
                _items.End();
                UpdateSelectedKey();
                return new TuiGroupEditorAction<TItem>(TuiGroupEditorActionKind.None);
            case ConsoleKey.Spacebar:
                return CreateSelectionAction(TuiGroupEditorActionKind.ToggleSelected);
            case ConsoleKey.Enter:
            case ConsoleKey.E:
                return CreateSelectionAction(TuiGroupEditorActionKind.EditSelected);
            case ConsoleKey.T:
                return CreateSelectionAction(TuiGroupEditorActionKind.RawEditSelected);
            case ConsoleKey.Escape:
                return new TuiGroupEditorAction<TItem>(TuiGroupEditorActionKind.Close);
            default:
                return new TuiGroupEditorAction<TItem>(TuiGroupEditorActionKind.None);
        }
    }

    private TuiGroupEditorAction<TItem> CreateSelectionAction(TuiGroupEditorActionKind kind)
    {
        if (!_items.TryGetSelected(out var selected) || _keySelector is null)
        {
            return new TuiGroupEditorAction<TItem>(TuiGroupEditorActionKind.SelectionUnavailable);
        }

        return new TuiGroupEditorAction<TItem>(kind, selected, _keySelector(selected));
    }

    private void UpdateSelectedKey()
    {
        if (_keySelector is null || !_items.TryGetSelected(out var selected))
        {
            return;
        }

        SelectedKey = _keySelector(selected);
    }
}
