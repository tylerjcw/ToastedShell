namespace Tosh.Cli.Tui;

internal sealed class TuiScrollState
{
    public int ItemCount { get; private set; }

    public int PageSize { get; private set; } = 1;

    public int Offset { get; private set; }

    public void SetDimensions(int itemCount, int pageSize)
    {
        ItemCount = Math.Max(0, itemCount);
        PageSize = Math.Max(1, pageSize);
        Offset = Math.Clamp(Offset, 0, GetMaxOffset());
    }

    public void EnsureVisible(int index)
    {
        if (ItemCount == 0)
        {
            Offset = 0;
            return;
        }

        var clampedIndex = Math.Clamp(index, 0, ItemCount - 1);

        if (clampedIndex < Offset)
        {
            Offset = clampedIndex;
            return;
        }

        var bottom = Offset + PageSize - 1;

        if (clampedIndex > bottom)
        {
            Offset = Math.Min(GetMaxOffset(), clampedIndex - PageSize + 1);
        }
    }

    public void LineUp() => Offset = Math.Max(0, Offset - 1);

    public void LineDown() => Offset = Math.Min(GetMaxOffset(), Offset + 1);

    public void PageUp() => Offset = Math.Max(0, Offset - PageSize);

    public void PageDown() => Offset = Math.Min(GetMaxOffset(), Offset + PageSize);

    public void Home() => Offset = 0;

    public void End() => Offset = GetMaxOffset();

    public (int Start, int Length) GetVisibleRange()
    {
        return (Offset, Math.Max(0, Math.Min(PageSize, ItemCount - Offset)));
    }

    private int GetMaxOffset()
    {
        return Math.Max(0, ItemCount - PageSize);
    }
}
