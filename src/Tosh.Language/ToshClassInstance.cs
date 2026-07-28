using Tosh.Runtime;
using System.Runtime.CompilerServices;

namespace Tosh.Language;

public sealed class ToshClassInstance : IShellRecordObject, IShellInvocableObject, IShellTypedObject, IShellEnumerableObject,
    IShellBinaryOperatorObject
    , ICloneable, IShellTypeCheckable
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _lazyInitialized = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lazyInitializationLock = new();
    private readonly Dictionary<string, TaskCompletionSource<object?>> _lazyInitializations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly AsyncLocal<IReadOnlySet<string>?> _activeLazyInitializers = new();
    private readonly HashSet<ToshClassDefinition> _constructingLayers =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<ToshClassDefinition> _constructedLayers =
        new(ReferenceEqualityComparer.Instance);
    private bool _clrBaseInitialized;

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
                var emptyBindings =
                    (IReadOnlyDictionary<string, Type?>)new Dictionary<string, Type?>(
                        StringComparer.OrdinalIgnoreCase);
                chain[parent] = emptyBindings;
                currentDef = parent;
                currentBindings = emptyBindings;
                continue;
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

    internal object? ClrBaseObject { get; private set; }

    internal bool IsConstructionLayerComplete(ToshClassDefinition definition) =>
        _constructedLayers.Contains(definition);

    internal bool TryBeginConstructionLayer(ToshClassDefinition definition) =>
        _constructingLayers.Add(definition);

    internal void CompleteConstructionLayer(ToshClassDefinition definition)
    {
        _constructingLayers.Remove(definition);
        _constructedLayers.Add(definition);
    }

    internal void AbortConstructionLayer(ToshClassDefinition definition) =>
        _constructingLayers.Remove(definition);

    internal bool TryInitializeClrBase(object clrBaseObject)
    {
        if (_clrBaseInitialized)
        {
            return false;
        }

        ClrBaseObject = clrBaseObject;
        _clrBaseInitialized = true;
        return true;
    }

    internal void CompleteInitialization() => IsInitializing = false;

    public IShellTypeDescriptor ShellTypeDescriptor => TypeArguments is { Count: > 0 }
        ? new BoundGenericTypeDescriptor(Definition, TypeArguments)
        : Definition;

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

    public ValueTask<IReadOnlyList<KeyValuePair<string, object?>>> GetMembersAsync(
        bool includeHidden,
        CancellationToken cancellationToken) =>
        Definition.GetInstanceMembersAsync(this, includeHidden, cancellationToken);

    public InvocationResult InvokeInstanceMethod(string methodName, IReadOnlyList<object?> arguments)
    {
        return Definition.InvokeInstanceMethod(this, methodName, arguments, includeHidden: false);
    }

    public ValueTask<InvocationResult> InvokeInstanceMethodAsync(
        string methodName,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken)
    {
        return Definition.InvokeInstanceMethodAsync(
            this,
            methodName,
            arguments,
            includeHidden: false,
            cancellationToken);
    }

    bool IShellBinaryOperatorObject.TryEvaluateBinaryOperator(
        string operatorName,
        object? other,
        out object? value) =>
        Definition.TryInvokeSpecialInstanceMethod(
            this,
            operatorName,
            [other],
            out value);

    public ValueTask<(bool Found, object? Value)> TryGetMemberAsync(
        string name,
        bool includeHidden,
        CancellationToken cancellationToken) =>
        Definition.TryGetInstanceMemberAsync(this, name, includeHidden, cancellationToken);

    public ValueTask<bool> TrySetMemberAsync(
        string name,
        object? value,
        CancellationToken cancellationToken) =>
        Definition.TrySetInstanceMemberAsync(
            this,
            name,
            value,
            includeHidden: false,
            cancellationToken);

    internal ValueTask<bool> TrySetMemberAsync(
        string name,
        object? value,
        bool includeHidden,
        CancellationToken cancellationToken) =>
        Definition.TrySetInstanceMemberAsync(this, name, value, includeHidden, cancellationToken);

    public bool HasShellItems => Definition.HasEnumerator;

    public IEnumerable<object?> EnumerateShellItems()
    {
        return Definition.EnumerateItems(this);
    }

    public IAsyncEnumerable<object?> EnumerateShellItemsAsync(
        CancellationToken cancellationToken)
    {
        return Definition.EnumerateItemsAsync(this, cancellationToken);
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

    internal bool TryGetStoredValue(string name, out object? value) => _values.TryGetValue(name, out value);

    internal void SetStoredValue(string name, object? value) => _values[name] = value;

    internal bool IsLazyInitializationActiveInCurrentContext(string name) =>
        _activeLazyInitializers.Value?.Contains(name) == true;

    internal IReadOnlySet<string>? EnterLazyInitializationContext(string name)
    {
        var previous = _activeLazyInitializers.Value;
        var active = previous is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(previous, StringComparer.OrdinalIgnoreCase);
        active.Add(name);
        _activeLazyInitializers.Value = active;
        return previous;
    }

    internal void ExitLazyInitializationContext(IReadOnlySet<string>? previous) =>
        _activeLazyInitializers.Value = previous;

    internal (bool IsOwner, Task<object?> Completion) GetOrCreateLazyInitialization(string name)
    {
        lock (_lazyInitializationLock)
        {
            if (_lazyInitialized.Contains(name))
            {
                _values.TryGetValue(name, out var value);
                return (false, Task.FromResult(value));
            }

            if (_lazyInitializations.TryGetValue(name, out var existing))
            {
                return (false, existing.Task);
            }

            var created = new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _lazyInitializations[name] = created;
            return (true, created.Task);
        }
    }

    internal void CompleteLazyInitialization(string name, object? value)
    {
        TaskCompletionSource<object?> completion;

        lock (_lazyInitializationLock)
        {
            completion = _lazyInitializations[name];
            _values[name] = value;
            _lazyInitialized.Add(name);
            _lazyInitializations.Remove(name);
        }

        completion.TrySetResult(value);
    }

    internal void FailLazyInitialization(string name, Exception exception)
    {
        TaskCompletionSource<object?>? completion;

        lock (_lazyInitializationLock)
        {
            _lazyInitializations.Remove(name, out completion);
        }

        if (completion is null)
        {
            return;
        }

        if (exception is OperationCanceledException canceled)
        {
            completion.TrySetCanceled(canceled.CancellationToken);
            return;
        }

        completion.TrySetException(exception);
        _ = completion.Task.Exception;
    }

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
