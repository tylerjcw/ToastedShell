using Tosh.Runtime;
using System.Runtime.CompilerServices;

namespace Tosh.Language;

public sealed class ToshClassInstance : IShellRecordObject, IShellInvocableObject, IShellTypedObject, IShellEnumerableObject,
    IShellBinaryOperatorObject
    , ICloneable, IShellTypeCheckable, IShellMemberDiagnostics, IShellClrDerivedObject
{
    /// <inheritdoc />
    /// <remarks>
    /// Walked rather than read off <see cref="Definition"/>, because the base belongs to
    /// whichever class in the chain named it: for `class E2 extends E1` over
    /// `class E1 extends Error`, `E2.ClrBaseType` is null and `E1`'s is not. Reading only
    /// this instance's own definition is the bug `TOAST-0018` describes on the `is` side.
    /// </remarks>
    Type? IShellClrDerivedObject.ClrBaseType
    {
        get
        {
            for (var current = Definition; current is not null; current = current.BaseClass)
            {
                if (current.ClrBaseType is { } clrBase)
                {
                    return clrBase;
                }
            }

            return null;
        }
    }

    /// <inheritdoc />
    object? IShellClrDerivedObject.ClrBaseInstance => ClrBaseObject;

    /// <summary>
    /// Says whether a member exists but was refused — <c>TS-P2-18</c>.
    /// </summary>
    /// <remarks>
    /// Answering null leaves the accessor's generic "was not found", which is the right message
    /// for a name this class genuinely does not declare.
    /// </remarks>
    public string? ExplainMissingMember(string name) => Definition.ExplainHiddenInstanceMember(name);

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

    internal ToshClassInstance(
        ToshClassDefinition definition,
        IReadOnlyDictionary<string, Type?>? typeArguments,
        IReadOnlyDictionary<string, string>? nominalTypeArguments = null)
    {
        Definition = definition;
        TypeArguments = typeArguments;
        NominalTypeArguments = nominalTypeArguments;

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

    /// <inheritdoc />
    public bool HasInstanceMember(string name) => Definition.HasInstanceMember(name);

    /// <summary>
    /// Resolved type-argument bindings for this instance, keyed by the
    /// class type-parameter name (e.g. <c>"T1"</c> → <c>typeof(int)</c>).
    /// <c>null</c> for non-generic classes; values may be <c>null</c> if a
    /// type-argument string did not resolve to a known CLR type (kept as a
    /// nominal binding for diagnostic display only).
    /// </summary>
    public IReadOnlyDictionary<string, Type?>? TypeArguments { get; }

    /// <summary>
    /// What each type parameter was closed over, written as the source wrote it —
    /// <c>TOAST-0125</c>.
    /// </summary>
    /// <remarks>
    /// A ToastScript class has no CLR type of its own, so `Holder&lt;Circle&gt;` binds
    /// nominally and <see cref="TypeArguments"/> holds null for it. That null was then read
    /// as "accept anything", which made the annotation unenforceable — and losing the name
    /// as well meant the instance could not even say what it had been closed over, so
    /// `type-of` answered `Holder&lt;T&gt;`.
    ///
    /// Resolving the name to a CLR type instead is what `TS-P2-39` was: the search reached
    /// every loaded assembly and found an unrelated type that merely shared the name. The
    /// name is kept here precisely so it can be checked *nominally*, against this instance's
    /// own declaration, rather than against whatever the CLR happens to have loaded.
    /// </remarks>
    public IReadOnlyDictionary<string, string>? NominalTypeArguments { get; }

    /// <summary>
    /// The nominal type-argument names to use for members declared on <paramref name="def"/>.
    /// </summary>
    /// <remarks>
    /// Only the instance's own definition carries them. An inherited generic member resolves
    /// through <see cref="GetBindingsFor"/> as before, which is unchanged.
    /// </remarks>
    internal IReadOnlyDictionary<string, string>? GetNominalBindingsFor(ToshClassDefinition def) =>
        ReferenceEquals(def, Definition) ? NominalTypeArguments : null;

    /// <summary>
    /// Whether this instance is closed over exactly the arguments written — <c>TOAST-0125</c>.
    /// </summary>
    /// <remarks>
    /// A CLR-bound parameter is compared by resolved type, so `int` and `System.Int32` are
    /// the same answer and `int` and `long` are not. A nominally-bound one is compared by
    /// name, which is all there is to compare.
    /// </remarks>
    private bool IsClosedOver(IReadOnlyList<string> written)
    {
        var names = Definition.TypeParameterNames;

        if (written.Count != names.Count)
        {
            return false;
        }

        for (var index = 0; index < written.Count; index++)
        {
            var parameter = names[index];

            if (TypeArguments is not null &&
                TypeArguments.TryGetValue(parameter, out var bound) &&
                bound is not null)
            {
                if (Definition.ResolveComparisonType(written[index]) != bound)
                {
                    return false;
                }

                continue;
            }

            if (NominalTypeArguments is null ||
                !NominalTypeArguments.TryGetValue(parameter, out var nominal) ||
                !string.Equals(nominal, written[index], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Splits a type-argument list on its top-level commas.</summary>
    private static List<string> SplitTopLevelArguments(string inner)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;

        for (var index = 0; index < inner.Length; index++)
        {
            switch (inner[index])
            {
                case '<': depth++; break;
                case '>': depth--; break;
                case ',' when depth == 0:
                    parts.Add(inner[start..index].Trim());
                    start = index + 1;
                    break;
            }
        }

        parts.Add(inner[start..].Trim());
        return parts;
    }

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

    /// <summary>
    /// Runs one of this object's methods to completion on the calling thread.
    /// </summary>
    /// <remarks>
    /// For a caller that has nowhere to await: a CLR virtual overridden by this class, which
    /// must return a value rather than a task. The same route a script function takes when a
    /// widget calls it back.
    /// </remarks>
    internal object? InvokeMethodOnThisThread(string methodName, IReadOnlyList<object?> arguments)
        => Definition.InvokeInstanceMethodOnThisThread(this, methodName, arguments);

    internal void CompleteInitialization() => IsInitializing = false;

    public IShellTypeDescriptor ShellTypeDescriptor => TypeArguments is { Count: > 0 }
        ? new BoundGenericTypeDescriptor(Definition, TypeArguments, NominalTypeArguments)
        : Definition;

    public string ShellTypeName => Definition.Name;

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        return Definition.TryGetInstanceMember(this, name, includeHidden, accessor: null, out value);
    }

    public bool TrySetMember(string name, object? value)
    {
        return Definition.TrySetInstanceMember(this, name, value, includeHidden: false, accessor: null);
    }

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        return Definition.GetInstanceMembers(this, includeHidden, accessor: null);
    }

    public ValueTask<IReadOnlyList<KeyValuePair<string, object?>>> GetMembersAsync(
        bool includeHidden,
        CancellationToken cancellationToken) =>
        Definition.GetInstanceMembersAsync(this, accessor: null, includeHidden, cancellationToken);

    public InvocationResult InvokeInstanceMethod(string methodName, IReadOnlyList<object?> arguments)
    {
        return Definition.InvokeInstanceMethod(this, methodName, arguments, includeHidden: false, accessor: null);
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
            accessor: null,
            cancellationToken);
    }

    /// <summary>
    /// Invokes a method with type arguments written at the call site — <c>$a.m&lt;int&gt;(11)</c>.
    /// Overriding this is what distinguishes a target that understands them from one that must
    /// refuse; the interface default refuses.
    /// </summary>
    public ValueTask<InvocationResult> InvokeInstanceMethodAsync(
        string methodName,
        IReadOnlyList<object?> arguments,
        IReadOnlyList<Type>? typeArguments,
        CancellationToken cancellationToken) =>
        Definition.InvokeInstanceMethodAsync(
            this,
            methodName,
            arguments,
            includeHidden: false,
            accessor: null,
            cancellationToken,
            typeArguments);

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
        Definition.TryGetInstanceMemberAsync(this, name, includeHidden, accessor: null, cancellationToken);

    public ValueTask<bool> TrySetMemberAsync(
        string name,
        object? value,
        CancellationToken cancellationToken) =>
        Definition.TrySetInstanceMemberAsync(
            this,
            name,
            value,
            includeHidden: false,
            accessor: null,
            cancellationToken);

    internal ValueTask<bool> TrySetMemberAsync(
        string name,
        object? value,
        bool includeHidden,
        CancellationToken cancellationToken) =>
        Definition.TrySetInstanceMemberAsync(this, name, value, includeHidden, accessor: null, cancellationToken);

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

        // `TOAST-0018`. A class declaring `equals` and no hash cannot use the reference
        // hash: `Equals` above would call two instances equal while they hashed apart,
        // and a container would then hold both. One shared bucket is correct instead —
        // slower within that bucket, never a wrong answer — and declaring `hash` is what
        // restores O(1).
        if (Definition.HasInstanceMember(nameof(Equals)))
        {
            return 0;
        }

        return RuntimeHelpers.GetHashCode(this);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>Display</c> first, then a declared <c>ToString</c>. Both are invoked with the
    /// class itself as the accessor, so a <c>shy</c> declaration is reachable: rendering a
    /// value is the type describing itself, not an outside caller reaching in.
    /// </remarks>
    public bool TryGetOwnRendering(out object? rendered)
    {
        var method =
            IsInstanceOf(ToastRenderer.DisplayTraitName) && Definition.HasInstanceMember(ToastRenderer.DisplayMethodName)
                ? ToastRenderer.DisplayMethodName
                : HasCustomToString() ? nameof(ToString) : null;

        if (method is null)
        {
            rendered = null;
            return false;
        }

        var result = Definition.InvokeInstanceMethod(
            this, method, Array.Empty<object?>(), includeHidden: true, accessor: Definition);

        rendered = result.Value;
        return !result.ReturnedVoid;
    }

    internal bool HasCustomToString()
    {
        return Definition.HasSpecialInstanceMethod(nameof(ToString), Array.Empty<object?>());
    }

    public object Clone()
    {
        var clone = new ToshClassInstance(Definition, TypeArguments, NominalTypeArguments);

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
        // `TOAST-0125`. A closed spelling asks two questions — is this a `Box`, and is it
        // closed over `int` — and the name walk below answers only the first. Comparing
        // rendered names instead answers neither reliably: `Box<String>` happens to match
        // `Box<string>` case-insensitively while `Box<Int32>` never matches `Box<int>`, so
        // `is` was true for one closure and false for another with no difference between
        // them.
        var angle = typeName.IndexOf('<', StringComparison.Ordinal);
        if (angle > 0 && typeName.EndsWith(">", StringComparison.Ordinal))
        {
            var openName = typeName[..angle];

            if (!IsInstanceOf(openName))
            {
                return false;
            }

            // The closure is compared only when the name is this instance's own class. For
            // an ancestor the written arguments belong to a different parameter list —
            // `class IntBox extends Box<int>` has none of its own — and the open-name match
            // is the honest answer there.
            if (!string.Equals(Definition.Name, openName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return IsClosedOver(SplitTopLevelArguments(typeName[(angle + 1)..^1]));
        }

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

            // `TOAST-0018`. At *each* level, not only the instance's own definition. A CLR
            // base is recorded on the class that names it, so for
            // `class E2 extends E1`, `class E1 extends Error` the CLR base lives on `E1`
            // and `E2.ClrBaseType` is null — and this check used to run once, after the
            // loop, against the instance's definition. Two levels of inheritance from a
            // built-in therefore matched nothing: `is Error`, `is ToshError` and
            // `is Exception` were all false for `E2`.
            if (current.ClrBaseType is { } clrBase && MatchesClrType(clrBase, typeName))
                return true;

            current = current.BaseClass;
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="typeName"/> names <paramref name="clrBase"/> or something it
    /// derives from — by alias, by name, or through an interface.
    /// </summary>
    private static bool MatchesClrType(Type clrBase, string typeName)
    {
        // `TOAST-0018`. The alias first. `Error` resolves to `ToshError`, so a class
        // declared `extends Error` matched `is ToshError` and `is Exception` while
        // `is Error` — the spelling it was declared with — was false. A user-defined error
        // was therefore indistinguishable from a thrown string, because
        // `catch (e) { if ($e is Error) ... }` put both in the same bucket.
        //
        // `DotNetTypeResolver` records the alias for exactly this purpose; the comment
        // beside its `error` entry already claimed `$e is NativeError` worked.
        if (DotNetTypeResolver.BuiltInAliases.TryGetValue(typeName.ToLowerInvariant(), out var aliased) &&
            aliased.IsAssignableFrom(clrBase))
        {
            return true;
        }

        for (var clrType = clrBase; clrType is not null; clrType = clrType.BaseType)
        {
            if (string.Equals(clrType.Name, typeName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(clrType.FullName, typeName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        foreach (var clrIface in clrBase.GetInterfaces())
        {
            if (string.Equals(clrIface.Name, typeName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(clrIface.FullName, typeName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
