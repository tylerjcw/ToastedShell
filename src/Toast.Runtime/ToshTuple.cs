using System.Collections;

namespace Tosh.Runtime;

public sealed class ToshTuple : IReadOnlyList<object?>, IShellTypedObject, IShellRecordObject
{
    private readonly object?[] _items;

    public ToshTuple(IEnumerable<object?> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = items.ToArray();
    }

    public int Count => _items.Length;

    public int Length => _items.Length;

    public object? this[int index] => _items[index];

    public IShellTypeDescriptor ShellTypeDescriptor => BuiltInShellTypes.Tuple;

    public string ShellTypeName => BuiltInShellTypes.Tuple.ShellTypeName;

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        if (string.Equals(name, nameof(Count), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, nameof(Length), StringComparison.OrdinalIgnoreCase))
        {
            value = Count;
            return true;
        }

        if (TryParseItemIndex(name, out var index))
        {
            value = index < _items.Length ? _items[index] : null;
            return true;
        }

        value = null;
        return false;
    }

    public bool TrySetMember(string name, object? value) => false;

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        var members = new List<KeyValuePair<string, object?>>(Count + 1)
        {
            new(nameof(Count), Count),
        };

        for (var index = 0; index < _items.Length; index++)
        {
            members.Add(new KeyValuePair<string, object?>($"Item{index + 1}", _items[index]));
        }

        return members;
    }

    public IEnumerator<object?> GetEnumerator()
    {
        foreach (var item in _items)
        {
            yield return item;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public object ToValueTuple()
    {
        return _items.Length switch
        {
            0 => ValueTuple.Create(),
            1 => ValueTuple.Create(_items[0]),
            2 => (_items[0], _items[1]),
            3 => (_items[0], _items[1], _items[2]),
            4 => (_items[0], _items[1], _items[2], _items[3]),
            5 => (_items[0], _items[1], _items[2], _items[3], _items[4]),
            6 => (_items[0], _items[1], _items[2], _items[3], _items[4], _items[5]),
            7 => (_items[0], _items[1], _items[2], _items[3], _items[4], _items[5], _items[6]),
            _ => CreateLargeValueTuple(),
        };
    }

    private object CreateLargeValueTuple()
    {
        // For 8+ items, nest into ValueTuple<..., TRest> using the standard .NET convention.
        // The last slot holds a nested ValueTuple containing the overflow items.
        if (_items.Length <= 7)
        {
            return ToValueTuple();
        }

        var rest = new ToshTuple(_items[7..]).ToValueTuple();
        return ValueTuple.Create(_items[0], _items[1], _items[2], _items[3], _items[4], _items[5], _items[6], rest);
    }

    private static bool TryParseItemIndex(string name, out int index)
    {
        index = -1;

        if (!name.StartsWith("Item", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(name["Item".Length..], out var parsed) ||
            parsed <= 0)
        {
            return false;
        }

        index = parsed - 1;
        return true;
    }
}
