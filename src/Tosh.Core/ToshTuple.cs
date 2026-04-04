using System.Collections;

namespace Tosh.Core;

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
