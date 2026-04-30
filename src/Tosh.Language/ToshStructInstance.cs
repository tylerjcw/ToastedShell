using Tosh.Runtime;

namespace Tosh.Language;

public sealed class ToshStructInstance : IShellRecordObject, IShellTypedObject, IShellInvocableObject, ICloneable, IEquatable<ToshStructInstance>
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);

    public ToshStructInstance(ToshStructDefinition definition)
    {
        Definition = definition;
    }

    public ToshStructDefinition Definition { get; }

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

        if (!Definition.IsFluid)
        {
            throw new InvalidOperationException($"Cannot modify field '{name}' on immutable struct '{Definition.Name}'. Declare the struct as 'fluid' to allow mutation.");
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

    public InvocationResult InvokeInstanceMethod(string methodName, IReadOnlyList<object?> arguments)
    {
        var method = Definition.Methods.FirstOrDefault(m =>
            !m.IsStatic &&
            string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase));

        if (method is null)
        {
            throw new InvalidOperationException($"Method '{methodName}' was not found on struct '{Definition.Name}'.");
        }

        var values = Definition.InvokeStructInstanceMethodAsync(this, method, arguments);
        var result = values.ToBlockingEnumerable().LastOrDefault();
        return new InvocationResult(result, ReturnedVoid: false);
    }

    /// <summary>
    /// Creates a deep copy of this struct instance (value-type semantics).
    /// </summary>
    public object Clone()
    {
        var clone = new ToshStructInstance(Definition);

        foreach (var (name, value) in _values)
        {
            clone._values[name] = value is ICloneable cloneable ? cloneable.Clone() : value;
        }

        return clone;
    }

    public bool Equals(ToshStructInstance? other)
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

    public override bool Equals(object? obj) => obj is ToshStructInstance other && Equals(other);

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
