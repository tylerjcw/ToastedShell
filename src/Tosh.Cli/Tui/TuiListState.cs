namespace Tosh.Cli.Tui;

internal sealed class TuiListState<T>
{
    private IReadOnlyList<T> _items = Array.Empty<T>();

    public TuiScrollState Scroll { get; } = new();

    public int SelectedIndex { get; private set; }

    public IReadOnlyList<T> Items => _items;

    public void SetItems(IReadOnlyList<T> items, int pageSize)
    {
        _items = items ?? throw new ArgumentNullException(nameof(items));

        if (_items.Count == 0)
        {
            SelectedIndex = 0;
        }
        else
        {
            SelectedIndex = Math.Clamp(SelectedIndex, 0, _items.Count - 1);
        }

        Scroll.SetDimensions(_items.Count, pageSize);
        Scroll.EnsureVisible(SelectedIndex);
    }

    public bool MovePrevious()
    {
        if (_items.Count == 0 || SelectedIndex == 0)
        {
            return false;
        }

        SelectedIndex--;
        Scroll.EnsureVisible(SelectedIndex);
        return true;
    }

    public bool MoveNext()
    {
        if (_items.Count == 0 || SelectedIndex >= _items.Count - 1)
        {
            return false;
        }

        SelectedIndex++;
        Scroll.EnsureVisible(SelectedIndex);
        return true;
    }

    public bool PageUp()
    {
        if (_items.Count == 0 || SelectedIndex == 0)
        {
            return false;
        }

        SelectedIndex = Math.Max(0, SelectedIndex - Scroll.PageSize);
        Scroll.PageUp();
        Scroll.EnsureVisible(SelectedIndex);
        return true;
    }

    public bool PageDown()
    {
        if (_items.Count == 0 || SelectedIndex >= _items.Count - 1)
        {
            return false;
        }

        SelectedIndex = Math.Min(_items.Count - 1, SelectedIndex + Scroll.PageSize);
        Scroll.PageDown();
        Scroll.EnsureVisible(SelectedIndex);
        return true;
    }

    public bool Home()
    {
        if (_items.Count == 0 || SelectedIndex == 0)
        {
            return false;
        }

        SelectedIndex = 0;
        Scroll.Home();
        return true;
    }

    public bool End()
    {
        if (_items.Count == 0 || SelectedIndex == _items.Count - 1)
        {
            return false;
        }

        SelectedIndex = _items.Count - 1;
        Scroll.End();
        Scroll.EnsureVisible(SelectedIndex);
        return true;
    }

    public bool SelectIndex(int index)
    {
        if (_items.Count == 0 || index < 0 || index >= _items.Count)
        {
            return false;
        }

        if (SelectedIndex == index)
        {
            Scroll.EnsureVisible(SelectedIndex);
            return true;
        }

        SelectedIndex = index;
        Scroll.EnsureVisible(SelectedIndex);
        return true;
    }

    public bool TryGetSelected(out T value)
    {
        if (_items.Count == 0)
        {
            value = default!;
            return false;
        }

        value = _items[SelectedIndex];
        return true;
    }
}
