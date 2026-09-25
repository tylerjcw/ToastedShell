using Tosh.Runtime;

namespace Tosh.Language;

public sealed class ToshRecordInstance : IShellRecordObject, IShellTypedObject, ICloneable, IEquatable<ToshRecordInstance>
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);

    public ToshRecordInstance(ToshRecordDefinition definition)
        : this(definition, typeArgumentBindings: null)
    {
    }

    public ToshRecordInstance(
        ToshRecordDefinition definition,
        IReadOnlyDictionary<string, Type?>? typeArgumentBindings)
    {
        Definition = definition;
        TypeArgumentBindings = typeArgumentBindings;
    }

    public ToshRecordDefinition Definition { get; }

    public IReadOnlyDictionary<string, Type?>? TypeArgumentBindings { get; }

    public IShellTypeDescriptor ShellTypeDescriptor => Definition;

    public string ShellTypeName => Definition.Name;

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        return _values.TryGetValue(name, out value);
    }

    public bool TrySetMember(string name, object? value)
    {
        if (Definition.IsStrict)
        {
            throw new InvalidOperationException($"Cannot modify field '{name}' on strict record '{Definition.Name}'.");
        }

        if (!Definition.TryGetField(name, out var field))
        {
            if (Definition.IsFluid)
            {
                _values[name] = value;
                return true;
            }

            return false;
        }

        _values[field.Name] = Definition.ConvertFieldValue(field, value, TypeArgumentBindings);
        return true;
    }

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        if (!Definition.IsFluid)
        {
            return Definition.Fields
                .Select(field => new KeyValuePair<string, object?>(field.Name, _values.TryGetValue(field.Name, out var value) ? value : null))
                .ToArray();
        }

        var result = new List<KeyValuePair<string, object?>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in Definition.Fields)
        {
            seen.Add(field.Name);
            result.Add(new KeyValuePair<string, object?>(field.Name, _values.TryGetValue(field.Name, out var value) ? value : null));
        }

        foreach (var (key, value) in _values)
        {
            if (seen.Add(key))
            {
                result.Add(new KeyValuePair<string, object?>(key, value));
            }
        }

        return result;
    }

    public object Clone()
    {
        var clone = new ToshRecordInstance(Definition, TypeArgumentBindings);

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

        if (Definition.IsFluid)
        {
            if (_values.Count != other._values.Count) return false;
            foreach (var (name, value) in _values)
            {
                if (!other._values.TryGetValue(name, out var otherValue) ||
                    !OperatorEvaluator.AreEqual(value, otherValue))
                {
                    return false;
                }
            }
            return true;
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

        if (Definition.IsFluid)
        {
            foreach (var (name, value) in _values.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                hash.Add(name, StringComparer.OrdinalIgnoreCase);
                hash.Add(value);
            }
        }
        else
        {
            foreach (var field in Definition.Fields)
            {
                hash.Add(field.Name, StringComparer.OrdinalIgnoreCase);
                hash.Add(_values.TryGetValue(field.Name, out var value) ? value : null);
            }
        }

        return hash.ToHashCode();
    }

    internal void SetStoredValue(string name, object? value) => _values[name] = value;

    internal bool RemoveStoredValue(string name) => _values.Remove(name);

    internal IReadOnlyDictionary<string, object?> GetStoredValues() => _values;
}
