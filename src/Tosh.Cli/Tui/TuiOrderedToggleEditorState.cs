namespace Tosh.Cli.Tui;

internal enum TuiOrderedToggleEditorActionKind
{
    None,
    Toggled,
    Reordered,
    Commit,
    Cancel,
    ToggleRejected,
    SelectionUnavailable,
}

internal readonly record struct TuiOrderedToggleEditorAction<TItem>(
    TuiOrderedToggleEditorActionKind Kind,
    TItem? Item = default,
    string? Key = null);

internal sealed class TuiOrderedToggleEditorState<TItem>
{
    private readonly List<TItem> _items = [];
    private Func<TItem, string>? _keySelector;
    private Func<TItem, bool>? _includedSelector;
    private Func<TItem, bool, TItem>? _includedUpdater;
    private int _minimumIncludedCount;

    public IReadOnlyList<TItem> Items => _items;

    public int SelectedIndex { get; private set; }

    public string? SelectedKey { get; private set; }

    public int PageSize { get; private set; } = 8;

    public void Open(
        IReadOnlyList<TItem> items,
        int pageSize,
        Func<TItem, string> keySelector,
        Func<TItem, bool> includedSelector,
        Func<TItem, bool, TItem> includedUpdater,
        string? preferredKey = null,
        int minimumIncludedCount = 0)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(keySelector);
        ArgumentNullException.ThrowIfNull(includedSelector);
        ArgumentNullException.ThrowIfNull(includedUpdater);

        _keySelector = keySelector;
        _includedSelector = includedSelector;
        _includedUpdater = includedUpdater;
        _minimumIncludedCount = Math.Max(0, minimumIncludedCount);
        PageSize = Math.Max(1, pageSize);

        _items.Clear();
        _items.AddRange(items);
        SelectPreferredItem(preferredKey);
    }

    public void Close()
    {
        _items.Clear();
        SelectedIndex = 0;
        SelectedKey = null;
    }

    public void SetPageSize(int pageSize)
    {
        PageSize = Math.Max(1, pageSize);
    }

    public TuiOrderedToggleEditorAction<TItem> HandleKey(ConsoleKeyInfo key)
    {
        if (_items.Count == 0)
        {
            return new TuiOrderedToggleEditorAction<TItem>(TuiOrderedToggleEditorActionKind.SelectionUnavailable);
        }

        switch (key.Key)
        {
            case ConsoleKey.UpArrow when key.Modifiers.HasFlag(ConsoleModifiers.Shift):
                return MoveSelected(-1);
            case ConsoleKey.DownArrow when key.Modifiers.HasFlag(ConsoleModifiers.Shift):
                return MoveSelected(1);
            case ConsoleKey.UpArrow:
                MoveSelection(-1);
                return new TuiOrderedToggleEditorAction<TItem>(TuiOrderedToggleEditorActionKind.None);
            case ConsoleKey.DownArrow:
                MoveSelection(1);
                return new TuiOrderedToggleEditorAction<TItem>(TuiOrderedToggleEditorActionKind.None);
            case ConsoleKey.PageUp:
                MoveSelection(-PageSize);
                return new TuiOrderedToggleEditorAction<TItem>(TuiOrderedToggleEditorActionKind.None);
            case ConsoleKey.PageDown:
                MoveSelection(PageSize);
                return new TuiOrderedToggleEditorAction<TItem>(TuiOrderedToggleEditorActionKind.None);
            case ConsoleKey.Home:
                SelectedIndex = 0;
                UpdateSelectedKey();
                return new TuiOrderedToggleEditorAction<TItem>(TuiOrderedToggleEditorActionKind.None);
            case ConsoleKey.End:
                SelectedIndex = _items.Count - 1;
                UpdateSelectedKey();
                return new TuiOrderedToggleEditorAction<TItem>(TuiOrderedToggleEditorActionKind.None);
            case ConsoleKey.Spacebar:
                return ToggleSelected();
            case ConsoleKey.Enter:
                return CreateSelectedAction(TuiOrderedToggleEditorActionKind.Commit);
            case ConsoleKey.Escape:
                return CreateSelectedAction(TuiOrderedToggleEditorActionKind.Cancel);
            default:
                return new TuiOrderedToggleEditorAction<TItem>(TuiOrderedToggleEditorActionKind.None);
        }
    }

    private void SelectPreferredItem(string? preferredKey)
    {
        if (_items.Count == 0)
        {
            SelectedIndex = 0;
            SelectedKey = null;
            return;
        }

        var selectedKey = preferredKey;

        if (selectedKey is null && _includedSelector is not null)
        {
            var includedIndex = _items.FindIndex(item => _includedSelector(item));
            selectedKey = includedIndex >= 0 && _keySelector is not null
                ? _keySelector(_items[includedIndex])
                : null;
        }

        if (selectedKey is not null && _keySelector is not null)
        {
            var selectedIndex = _items.FindIndex(item => string.Equals(_keySelector(item), selectedKey, StringComparison.OrdinalIgnoreCase));

            if (selectedIndex >= 0)
            {
                SelectedIndex = selectedIndex;
                UpdateSelectedKey();
                return;
            }
        }

        SelectedIndex = 0;
        UpdateSelectedKey();
    }

    private void MoveSelection(int delta)
    {
        SelectedIndex = Math.Clamp(SelectedIndex + delta, 0, _items.Count - 1);
        UpdateSelectedKey();
    }

    private TuiOrderedToggleEditorAction<TItem> MoveSelected(int direction)
    {
        var newIndex = Math.Clamp(SelectedIndex + direction, 0, _items.Count - 1);

        if (newIndex == SelectedIndex)
        {
            return new TuiOrderedToggleEditorAction<TItem>(TuiOrderedToggleEditorActionKind.None);
        }

        (_items[SelectedIndex], _items[newIndex]) = (_items[newIndex], _items[SelectedIndex]);
        SelectedIndex = newIndex;
        UpdateSelectedKey();
        return CreateSelectedAction(TuiOrderedToggleEditorActionKind.Reordered);
    }

    private TuiOrderedToggleEditorAction<TItem> ToggleSelected()
    {
        if (_includedSelector is null || _includedUpdater is null)
        {
            return new TuiOrderedToggleEditorAction<TItem>(TuiOrderedToggleEditorActionKind.SelectionUnavailable);
        }

        var selectedItem = _items[SelectedIndex];
        var isIncluded = _includedSelector(selectedItem);

        if (isIncluded && _items.Count(_includedSelector) <= _minimumIncludedCount)
        {
            return CreateSelectedAction(TuiOrderedToggleEditorActionKind.ToggleRejected);
        }

        _items[SelectedIndex] = _includedUpdater(selectedItem, !isIncluded);
        UpdateSelectedKey();
        return CreateSelectedAction(TuiOrderedToggleEditorActionKind.Toggled);
    }

    private TuiOrderedToggleEditorAction<TItem> CreateSelectedAction(TuiOrderedToggleEditorActionKind kind)
    {
        if (_keySelector is null || _items.Count == 0)
        {
            return new TuiOrderedToggleEditorAction<TItem>(TuiOrderedToggleEditorActionKind.SelectionUnavailable);
        }

        var selectedItem = _items[SelectedIndex];
        return new TuiOrderedToggleEditorAction<TItem>(kind, selectedItem, _keySelector(selectedItem));
    }

    private void UpdateSelectedKey()
    {
        if (_keySelector is null || _items.Count == 0)
        {
            SelectedKey = null;
            return;
        }

        SelectedIndex = Math.Clamp(SelectedIndex, 0, _items.Count - 1);
        SelectedKey = _keySelector(_items[SelectedIndex]);
    }
}
