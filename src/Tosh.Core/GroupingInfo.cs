namespace Tosh.Core;

public sealed record GroupingInfo(object? Key, IReadOnlyList<object?> Items)
{
    public int Count => Items.Count;
}
