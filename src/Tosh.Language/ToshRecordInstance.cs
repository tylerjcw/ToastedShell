using Tosh.Runtime;

namespace Tosh.Language;

public sealed class ToshRecordInstance : IShellRecordObject, IShellTypedObject, ICloneable, IEquatable<ToshRecordInstance>
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);

    public ToshRecordInstance(ToshRecordDefinition definition)
    {
        Definition = definition;
    }

    public ToshRecordDefinition Definition { get; }

    public IShellTypeDescriptor ShellTypeDescriptor => Definition;

    public string ShellTypeName => Definition.Name;

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        return _values.TryGetValue(name, out value);
    }

    public bool TrySetMember(string name, object? value)
    {
        if (!Definition.TryGetField(name, out var field))
        {
            return false;
        }

        _values[field.Name] = Definition.ConvertFieldValue(field, value);
        return true;
    }

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        return Definition.Fields
            .Select(field => new KeyValuePair<string, object?>(field.Name, _values.TryGetValue(field.Name, out var value) ? value : null))
            .ToArray();
    }

    public object Clone()
    {
        var clone = new ToshRecordInstance(Definition);

        foreach (var (name, value) in _values)
        {
            clone._values[name] = value;
        }

        return clone;
    }

    public bool Equals(ToshRecordInstance? other)
    {
        if (other is null || !ReferenceEquals(Definition, other.Definition))
        {
            return false;
        }

        return Definition.Fields.All(field =>
            OperatorEvaluator.AreEqual(
                _values.TryGetValue(field.Name, out var left) ? left : null,
                other._values.TryGetValue(field.Name, out var right) ? right : null));
    }

    public override bool Equals(object? obj) => obj is ToshRecordInstance other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Definition);

        foreach (var field in Definition.Fields)
        {
            hash.Add(field.Name, StringComparer.OrdinalIgnoreCase);
            hash.Add(_values.TryGetValue(field.Name, out var value) ? value : null);
        }

        return hash.ToHashCode();
    }

    internal void SetStoredValue(string name, object? value) => _values[name] = value;
}
