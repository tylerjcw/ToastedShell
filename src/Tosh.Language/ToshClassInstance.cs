using Tosh.Core;
using System.Runtime.CompilerServices;

namespace Tosh.Language;

public sealed class ToshClassInstance : IShellRecordObject, IShellInvocableObject, IShellTypedObject, IShellEnumerableObject
    , ICloneable, IShellTypeCheckable
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _lazyInitialized = new(StringComparer.OrdinalIgnoreCase);
    private bool _superWasCalled;

    public ToshClassInstance(ToshClassDefinition definition)
    {
        Definition = definition;
    }

    public ToshClassDefinition Definition { get; }

    internal bool IsInitializing { get; private set; } = true;

    internal void MarkSuperCalled() => _superWasCalled = true;

    internal object? ClrBaseObject { get; set; }

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

        foreach (var name in _lazyInitialized)
        {
            clone._lazyInitialized.Add(name);
        }

        return clone;
    }

    internal void Initialize(IReadOnlyDictionary<string, object?> constructorLocals, ToshClassConstructorDefinition? constructor)
    {
        // Initialize base class properties first
        InitializeBaseClassProperties(Definition.BaseClass, constructorLocals);

        foreach (var property in Definition.Properties)
        {
            if (property.IsComputed || property.IsStatic || property.IsLazy || property.IsAbstract)
            {
                continue;
            }

            var initialValue = Definition.GetInitialPropertyValue(this, property, constructorLocals);
            _values[property.Name] = initialValue;
        }

        // Auto-call parent constructor with extends clause args if present
        if (Definition.BaseConstructorArgs is { Count: > 0 } && Definition.BaseClass is not null)
        {
            var args = Definition.EvaluateBaseConstructorArgs(constructorLocals);
            Definition.BaseClass.InvokeConstructorOnInstance(this, args);
            _superWasCalled = true;
        }

        if (constructor is not null)
        {
            Definition.RunConstructor(this, constructor, constructorLocals);
        }

        // Validate that $super() was called if the parent has a primary constructor and no extends clause args
        if (Definition.BaseClass is not null &&
            Definition.BaseConstructorArgs is null or { Count: 0 } &&
            Definition.BaseClass.HasPrimaryConstructor &&
            !_superWasCalled)
        {
            throw new InvalidOperationException(
                $"Class '{Definition.Name}' extends '{Definition.BaseClass.Name}' which has a primary constructor, " +
                $"but $super() was never called. Either provide arguments in the extends clause " +
                $"(e.g., 'extends {Definition.BaseClass.Name}(args)') or call $super(args) in the constructor body.");
        }

        IsInitializing = false;
    }

    private void InitializeBaseClassProperties(ToshClassDefinition? baseClass, IReadOnlyDictionary<string, object?> constructorLocals)
    {
        if (baseClass is null) return;

        // Recurse up first so root properties init first
        InitializeBaseClassProperties(baseClass.BaseClass, constructorLocals);

        foreach (var property in baseClass.Properties)
        {
            if (property.IsComputed || property.IsStatic || property.IsLazy || property.IsAbstract) continue;
            if (_values.ContainsKey(property.Name)) continue;

            var initialValue = baseClass.GetInitialPropertyValue(this, property, constructorLocals);
            _values[property.Name] = initialValue;
        }
    }

    internal bool TryGetStoredValue(string name, out object? value) => _values.TryGetValue(name, out value);

    internal void SetStoredValue(string name, object? value) => _values[name] = value;

    internal bool IsLazyInitialized(string name) => _lazyInitialized.Contains(name);

    internal void MarkLazyInitialized(string name) => _lazyInitialized.Add(name);

    public bool IsInstanceOf(string typeName)
    {
        // Walk the Tosh class hierarchy
        var current = Definition;
        while (current is not null)
        {
            if (string.Equals(current.Name, typeName, StringComparison.OrdinalIgnoreCase))
                return true;

            // Check implemented interfaces at each level
            foreach (var iface in current.ImplementedInterfaces)
            {
                if (string.Equals(iface.Name, typeName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // Check used traits at each level
            foreach (var trait in current.UsedTraits)
            {
                if (string.Equals(trait.Name, typeName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            current = current.BaseClass;
        }

        // Check CLR base type if present
        if (Definition.ClrBaseType is { } clrBase)
        {
            if (string.Equals(clrBase.Name, typeName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(clrBase.FullName, typeName, StringComparison.OrdinalIgnoreCase))
                return true;

            // Walk CLR base type hierarchy
            for (var clrType = clrBase.BaseType; clrType is not null; clrType = clrType.BaseType)
            {
                if (string.Equals(clrType.Name, typeName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(clrType.FullName, typeName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // Check CLR interfaces
            foreach (var clrIface in clrBase.GetInterfaces())
            {
                if (string.Equals(clrIface.Name, typeName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(clrIface.FullName, typeName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }
}
