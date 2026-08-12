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
        if (Definition.TryGetField(name, out var field))
        {
            if (!Definition.IsFluid && !IsConstructing)
            {
                throw new InvalidOperationException($"Cannot modify field '{name}' on immutable struct '{Definition.Name}'. Declare the struct as 'fluid' to allow mutation.");
            }

            _values[field.Name] = Definition.ConvertFieldValue(field, value);
            return true;
        }

        // `TS-P2-83`. Declared properties were unreachable for writing: this knew only fields, so
        // `$s.X = 9` fell through to reflection and reported the member missing on a struct that
        // reads `$s.X` perfectly well. The same omission `GetMembers` carried until it was fixed
        // to list properties alongside fields — introspection and behaviour disagreeing, one
        // method over.
        foreach (var property in Definition.Properties)
        {
            if (property.IsStatic || property.IsComputed) continue;
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;

            if (!Definition.IsFluid && !IsConstructing)
            {
                throw new InvalidOperationException(
                    $"Cannot modify property '{property.Name}' on immutable struct '{Definition.Name}'. " +
                    "Declare the struct as 'fluid' to allow mutation.");
            }

            _values[property.Name] = value;
            return true;
        }

        return false;
    }

    /// <summary>
    /// True while the struct is being constructed — <c>TS-P2-83</c>.
    /// </summary>
    /// <remarks>
    /// A constructor writing its own fields is construction, not mutation, so the immutability
    /// guard must not refuse it. Field binding already bypassed the guard by writing the backing
    /// store directly; a constructor body cannot, since it goes through ordinary assignment.
    /// </remarks>
    internal bool IsConstructing { get; set; }

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        // Fields and declared properties both, in declaration order. Listing only fields meant
        // `$p | members` reported nothing for a struct whose properties `$p.X` reads perfectly
        // well — introspection contradicting behaviour, which is the `TS-P1-33` shape.
        var members = new List<KeyValuePair<string, object?>>(
            Definition.Fields.Count + Definition.Properties.Count);

        foreach (var field in Definition.Fields)
        {
            members.Add(new KeyValuePair<string, object?>(
                field.Name,
                _values.TryGetValue(field.Name, out var value) ? value : null));
        }

        foreach (var property in Definition.Properties)
        {
            if (property.IsStatic || (property.IsShy && !includeHidden))
            {
                continue;
            }

            members.Add(new KeyValuePair<string, object?>(
                property.Name,
                _values.TryGetValue(property.Name, out var value) ? value : null));
        }

        return members;
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
