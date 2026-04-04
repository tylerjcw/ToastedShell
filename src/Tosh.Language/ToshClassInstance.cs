using Tosh.Core;
using System.Runtime.CompilerServices;

namespace Tosh.Language;

public sealed class ToshClassInstance : IShellRecordObject, IShellInvocableObject, IShellTypedObject, IShellEnumerableObject
    , ICloneable
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);

    public ToshClassInstance(ToshClassDefinition definition)
    {
        Definition = definition;
    }

    public ToshClassDefinition Definition { get; }

    public IShellTypeDescriptor ShellTypeDescriptor => Definition;

    public string ShellTypeName => Definition.Name;

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        return Definition.TryGetInstanceMember(this, name, includeHidden, out value);
    }

    public bool TrySetMember(string name, object? value)
    {
        return Definition.TrySetInstanceMember(this, name, value, includeHidden: false);
    }

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        return Definition.GetInstanceMembers(this, includeHidden);
    }

    public InvocationResult InvokeInstanceMethod(string methodName, IReadOnlyList<object?> arguments)
    {
        return Definition.InvokeInstanceMethod(this, methodName, arguments, includeHidden: false);
    }

    public IEnumerable<object?> EnumerateShellItems()
    {
        return Definition.EnumerateItems(this);
    }

    public override string ToString()
    {
        if (Definition.TryInvokeSpecialInstanceMethod(this, nameof(ToString), Array.Empty<object?>(), out var value))
        {
            return value?.ToString() ?? string.Empty;
        }

        return Definition.Name;
    }

    public override bool Equals(object? obj)
    {
        if (Definition.TryInvokeSpecialInstanceMethod(this, nameof(Equals), [obj], out var value))
        {
            return OperatorEvaluator.ToBoolean(value);
        }

        return ReferenceEquals(this, obj);
    }

    public override int GetHashCode()
    {
        if (Definition.TryInvokeSpecialInstanceMethod(this, nameof(GetHashCode), Array.Empty<object?>(), out var value))
        {
            if (TypeConversion.TryConvert(value, typeof(int), out var converted) && converted is int hashCode)
            {
                return hashCode;
            }

            return value?.GetHashCode() ?? 0;
        }

        return RuntimeHelpers.GetHashCode(this);
    }

    internal bool HasCustomToString()
    {
        return Definition.HasSpecialInstanceMethod(nameof(ToString), Array.Empty<object?>());
    }

    public object Clone()
    {
        var clone = new ToshClassInstance(Definition);

        foreach (var (name, value) in _values)
        {
            clone._values[name] = value;
        }

        return clone;
    }

    internal void Initialize(IReadOnlyDictionary<string, object?> constructorLocals, ToshClassConstructorDefinition? constructor)
    {
        foreach (var property in Definition.Properties)
        {
            if (property.IsComputed)
            {
                continue;
            }

            var initialValue = Definition.GetInitialPropertyValue(this, property, constructorLocals);
            _values[property.Name] = initialValue;
        }

        if (constructor is not null)
        {
            Definition.RunConstructor(this, constructor, constructorLocals);
        }
    }

    internal bool TryGetStoredValue(string name, out object? value) => _values.TryGetValue(name, out value);

    internal void SetStoredValue(string name, object? value) => _values[name] = value;
}
