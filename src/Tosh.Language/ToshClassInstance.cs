using Tosh.Runtime;
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

    internal ToshClassInstance(ToshClassDefinition definition, IReadOnlyDictionary<string, Type?>? typeArguments)
    {
        Definition = definition;
        TypeArguments = typeArguments;

        // Build the binding chain whenever this class itself or any
        // ancestor declares type parameters; otherwise lookups never
        // resolve substitutions for inherited generic properties on
        // non-generic descendants (e.g. `class IntChild extends Base<int>`).
        if (typeArguments is not null || HasAnyGenericAncestor(definition))
        {
            _bindingChain = BuildBindingChain(
                definition,
                typeArguments ?? (IReadOnlyDictionary<string, Type?>)new Dictionary<string, Type?>(StringComparer.OrdinalIgnoreCase));
        }
    }

    private static bool HasAnyGenericAncestor(ToshClassDefinition def)
    {
        for (var c = def; c is not null; c = c.BaseClass)
        {
            if (c.TypeParameterNames.Count > 0) return true;
        }
        return false;
    }

    private readonly Dictionary<ToshClassDefinition, IReadOnlyDictionary<string, Type?>>? _bindingChain;

    /// <summary>
    /// Returns the type-argument bindings to apply when resolving member type
    /// names declared on <paramref name="def"/>. For the instance's own
    /// definition this is identical to <see cref="TypeArguments"/>; for an
    /// ancestor declared via <c>extends Foo&lt;T1, T2&gt;</c>, the bindings
    /// are derived by substituting the child's bindings into the
    /// <see cref="ToshClassDefinition.BaseTypeArguments"/> strings.
    /// </summary>
    internal IReadOnlyDictionary<string, Type?>? GetBindingsFor(ToshClassDefinition def)
    {
        if (_bindingChain is null) return null;
        return _bindingChain.TryGetValue(def, out var bindings) ? bindings : null;
    }

    private static Dictionary<ToshClassDefinition, IReadOnlyDictionary<string, Type?>> BuildBindingChain(
        ToshClassDefinition leaf,
        IReadOnlyDictionary<string, Type?> leafBindings)
    {
        var chain = new Dictionary<ToshClassDefinition, IReadOnlyDictionary<string, Type?>>(ReferenceEqualityComparer.Instance)
        {
            [leaf] = leafBindings,
        };

        var currentDef = leaf;
        var currentBindings = leafBindings;

        while (currentDef.BaseClass is { } parent)
        {
            // Need parent bindings: parent's TypeParameterNames[i] →
            // resolve currentDef.BaseTypeArguments[i] using currentBindings.
            if (parent.TypeParameterNames.Count == 0)
            {
                break;
            }

            var baseArgs = currentDef.BaseTypeArguments;
            var baseResolved = currentDef.BaseTypeArgumentsResolved;
            if (baseArgs is null || baseArgs.Count != parent.TypeParameterNames.Count)
            {
                // arity mismatch was checked at class-binding time; if we get
                // here treat parent as unbound (no substitution).
                break;
            }

            var parentBindings = new Dictionary<string, Type?>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < parent.TypeParameterNames.Count; i++)
            {
                var argString = baseArgs[i];
                // If the argument is itself a type-parameter of currentDef,
                // forward its current binding. Otherwise prefer the
                // pre-resolved CLR type captured at class-binding time.
                if (currentBindings.TryGetValue(argString, out var bound))
                {
                    parentBindings[parent.TypeParameterNames[i]] = bound;
                }
                else
                {
                    parentBindings[parent.TypeParameterNames[i]] =
                        baseResolved is not null && i < baseResolved.Count ? baseResolved[i] : null;
                }
            }

            chain[parent] = parentBindings;
            currentDef = parent;
            currentBindings = parentBindings;
        }

        return chain;
    }

    public ToshClassDefinition Definition { get; }

    /// <summary>
    /// Resolved type-argument bindings for this instance, keyed by the
    /// class type-parameter name (e.g. <c>"T1"</c> → <c>typeof(int)</c>).
    /// <c>null</c> for non-generic classes; values may be <c>null</c> if a
    /// type-argument string did not resolve to a known CLR type (kept as a
    /// nominal binding for diagnostic display only).
    /// </summary>
    public IReadOnlyDictionary<string, Type?>? TypeArguments { get; }

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
        var clone = new ToshClassInstance(Definition, TypeArguments);

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
