using Tosh.Runtime;
using Tosh.Language.Parsing;

namespace Tosh.Language;

public sealed class ToshClassDefinition : IShellNamedType
{
    private readonly ToshEngine _engine;
    private readonly Dictionary<string, ToshClassPropertyDefinition> _propertiesByName;
    private readonly Dictionary<string, IReadOnlyList<ToshClassMethodDefinition>> _methodsByName;
    private readonly List<ToshClassConstructorDefinition> _constructors;
    private readonly IReadOnlyList<FunctionParameterDefinition> _primaryConstructorParameters;
    private readonly Dictionary<string, object?> _staticValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ToshClassPropertyDefinition> _properties;
    private readonly List<ToshClassMethodDefinition> _methods;

    public ToshClassDefinition(
        ToshEngine engine,
        string name,
        IReadOnlyList<FunctionParameterDefinition> primaryConstructorParameters,
        IReadOnlyList<ToshClassPropertyDefinition> properties,
        IReadOnlyList<ToshClassMethodDefinition> methods,
        IReadOnlyList<ToshClassConstructorDefinition> constructors,
        string sourceName,
        string sourceText,
        TextSpan span,
        IReadOnlyList<LexicalScope>? capturedScopes,
        IReadOnlyList<string>? typeParameters = null,
        IReadOnlyList<ToshTypeParameterConstraint>? typeParameterConstraints = null)
    {
        _engine = engine;
        Name = name;
        TypeParameterNames = typeParameters ?? Array.Empty<string>();
        TypeParameterConstraints = typeParameterConstraints ?? Array.Empty<ToshTypeParameterConstraint>();
        _primaryConstructorParameters = primaryConstructorParameters;
        _properties = new List<ToshClassPropertyDefinition>(properties);
        _methods = new List<ToshClassMethodDefinition>(methods);
        _constructors = new List<ToshClassConstructorDefinition>(constructors);
        Properties = _properties;
        Methods = _methods;
        SourceName = sourceName;
        SourceText = sourceText;
        Span = span;
        CapturedScopes = capturedScopes;
        _propertiesByName = properties.ToDictionary(property => property.Name, StringComparer.OrdinalIgnoreCase);
        _methodsByName = methods
            .GroupBy(method => method.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ToshClassMethodDefinition>)group.ToArray(), StringComparer.OrdinalIgnoreCase);

        // Initialize static property storage with defaults
        foreach (var prop in properties.Where(p => p.IsStatic && p.Initializer is not null && !p.IsComputed))
        {
            _staticValues[prop.Name] = null; // Will be evaluated lazily on first access
        }
    }

    public string Name { get; }

    public IReadOnlyList<string> TypeParameterNames { get; }

    /// <summary>
    /// Trait-style constraints declared via <c>where T: Constraint, …</c>.
    /// Validated at instantiation; see
    /// <see cref="ToshTypeParameterConstraintRegistry"/>.
    /// </summary>
    public IReadOnlyList<ToshTypeParameterConstraint> TypeParameterConstraints { get; }

    public IReadOnlyList<ToshClassPropertyDefinition> Properties { get; }

    public IReadOnlyList<ToshClassMethodDefinition> Methods { get; }

    public bool HasPrimaryConstructor => _primaryConstructorParameters.Count > 0;

    /// <summary>
    /// Primary-constructor parameter declarations, in declaration order.
    /// Empty when the class has no primary constructor (i.e. when ctors
    /// are declared as named-method blocks). Used by call-site type
    /// inference to back-infer generic type arguments from positional
    /// constructor arguments.
    /// </summary>
    internal IReadOnlyList<FunctionParameterDefinition> PrimaryConstructorParameters
        => _primaryConstructorParameters;

    public string SourceName { get; }

    public string SourceText { get; }

    public TextSpan Span { get; }

    public IReadOnlyList<LexicalScope>? CapturedScopes { get; }

    public IReadOnlyList<ToshInterfaceDefinition> ImplementedInterfaces { get; internal set; } = Array.Empty<ToshInterfaceDefinition>();

    public IReadOnlyList<ToshTraitDefinition> UsedTraits { get; internal set; } = Array.Empty<ToshTraitDefinition>();

    public IReadOnlyList<PipelineSyntax>? BaseConstructorArgs { get; internal set; }

    /// <summary>
    /// Type-argument expressions written on the <c>extends Foo&lt;T1, T2&gt;</c>
    /// clause, captured as raw strings (e.g. <c>"int"</c>, <c>"T1"</c>,
    /// <c>"list&lt;string&gt;"</c>). Resolved at construction time using the
    /// child instance's own type-argument bindings.
    /// </summary>
    public IReadOnlyList<string>? BaseTypeArguments { get; internal set; }

    /// <summary>
    /// Eagerly-resolved CLR types matching <see cref="BaseTypeArguments"/>.
    /// Each entry is the resolved <see cref="Type"/> for a concrete name
    /// (e.g. <c>"int"</c> -&gt; <c>typeof(int)</c>) or <c>null</c> when the
    /// corresponding argument is itself a type-parameter of the child class
    /// (forwarded at construction time) or could not be resolved.
    /// </summary>
    public IReadOnlyList<Type?>? BaseTypeArgumentsResolved { get; internal set; }

    public ToshClassDefinition? BaseClass { get; internal set; }

    public Type? ClrBaseType { get; internal set; }

    public bool IsSealed { get; internal set; }

    public bool IsAbstract { get; internal set; }

    public bool IsHermit { get; internal set; }

    public bool IsStrict { get; internal set; }

    public bool IsPartial { get; internal set; }

    /// <summary>
    /// Merges members from another partial class definition into this one.
    /// Properties, methods, and constructors from the other definition are added.
    /// </summary>
    internal void MergePartial(
        IReadOnlyList<ToshClassPropertyDefinition> properties,
        IReadOnlyList<ToshClassMethodDefinition> methods,
        IReadOnlyList<ToshClassConstructorDefinition> constructors)
    {
        foreach (var property in properties)
        {
            if (!_propertiesByName.ContainsKey(property.Name))
            {
                _properties.Add(property);
                _propertiesByName[property.Name] = property;

                if (property.IsStatic && property.Initializer is not null && !property.IsComputed)
                {
                    _staticValues[property.Name] = null;
                }
            }
        }

        foreach (var method in methods)
        {
            _methods.Add(method);
            if (_methodsByName.TryGetValue(method.Name, out var existing))
            {
                var combined = new List<ToshClassMethodDefinition>(existing) { method };
                _methodsByName[method.Name] = combined;
            }
            else
            {
                _methodsByName[method.Name] = new[] { method };
            }
        }

        foreach (var constructor in constructors)
        {
            _constructors.Add(constructor);
        }
    }

    /// <summary>
    /// Adds a single method (e.g. from a trait default implementation).
    /// </summary>
    internal void AddMethod(ToshClassMethodDefinition method)
    {
        _methods.Add(method);
        if (_methodsByName.TryGetValue(method.Name, out var existing))
        {
            var combined = new List<ToshClassMethodDefinition>(existing) { method };
            _methodsByName[method.Name] = combined;
        }
        else
        {
            _methodsByName[method.Name] = new[] { method };
        }
    }

    /// <summary>
    /// Adds a single property (e.g. from a trait default property).
    /// </summary>
    internal void AddProperty(ToshClassPropertyDefinition property)
    {
        if (!_propertiesByName.ContainsKey(property.Name))
        {
            _properties.Add(property);
            _propertiesByName[property.Name] = property;
        }
    }

    public string ShellTypeName => Name;

    public string ShellFullName => Name;

    public string? ShellNamespace => null;

    public string? ShellAssemblyName => "ToSh";

    public string? ShellBaseTypeName => BaseClass?.Name ?? ClrBaseType?.FullName ?? typeof(object).FullName;

    public bool ShellIsClass => true;

    public bool ShellIsInterface => false;

    public bool ShellIsEnum => false;

    public bool ShellIsValueType => false;

    public bool ShellIsAbstract => IsAbstract;

    public bool ShellIsGenericType => TypeParameterNames.Count > 0;

    public bool ShellIsArray => false;

    public bool ShellIsPublic => true;

    public object CreateInstance(IReadOnlyList<object?> arguments)
    {
        return CreateInstanceAsync(arguments, CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();
    }

    public ValueTask<object> CreateInstanceAsync(
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken)
    {
        return new ValueTask<object>(
            CreateInstanceCoreAsync(arguments, typeArgumentBindings: null, cancellationToken));
    }

    /// <summary>
    /// Generic-aware construction. <paramref name="resolvedTypeArguments"/>
    /// must have <see cref="TypeParameterNames"/>.Count entries, in declaration
    /// order. Each entry is the resolved CLR <see cref="Type"/> for that
    /// parameter, or <c>null</c> when the user-supplied type-argument string
    /// could not be resolved (e.g. another open generic). <paramref
    /// name="typeArgumentDisplay"/> mirrors the original strings for
    /// diagnostic messages.
    /// </summary>
    public object CreateGenericInstance(
        IReadOnlyList<Type?> resolvedTypeArguments,
        IReadOnlyList<string> typeArgumentDisplay,
        IReadOnlyList<object?> arguments)
    {
        return CreateGenericInstanceAsync(
                resolvedTypeArguments,
                typeArgumentDisplay,
                arguments,
                CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();
    }

    public async ValueTask<object> CreateGenericInstanceAsync(
        IReadOnlyList<Type?> resolvedTypeArguments,
        IReadOnlyList<string> typeArgumentDisplay,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken)
    {
        if (resolvedTypeArguments.Count != TypeParameterNames.Count)
        {
            throw new InvalidOperationException(
                $"Generic class '{Name}' expects {TypeParameterNames.Count} type argument(s) " +
                $"<{string.Join(", ", TypeParameterNames)}> but received {resolvedTypeArguments.Count}.");
        }

        var bindings = new Dictionary<string, Type?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < TypeParameterNames.Count; i++)
        {
            bindings[TypeParameterNames[i]] = resolvedTypeArguments[i];
        }

        ValidateTypeParameterConstraints(bindings, typeArgumentDisplay);

        return await CreateInstanceCoreAsync(arguments, bindings, cancellationToken);
    }

    private void ValidateTypeParameterConstraints(
        IReadOnlyDictionary<string, Type?> bindings,
        IReadOnlyList<string> typeArgumentDisplay)
    {
        if (TypeParameterConstraints.Count == 0) return;
        foreach (var clause in TypeParameterConstraints)
        {
            var displayIndex = TypeParameterNames
                .Select((n, i) => (n, i))
                .FirstOrDefault(t => string.Equals(t.n, clause.TypeParameter, StringComparison.OrdinalIgnoreCase)).i;
            var argDisplay = displayIndex < typeArgumentDisplay.Count
                ? typeArgumentDisplay[displayIndex]
                : clause.TypeParameter;

            bindings.TryGetValue(clause.TypeParameter, out var bound);

            foreach (var constraintName in clause.ConstraintNames)
            {
                bool satisfied;
                bool known;
                if (ToshTypeParameterConstraintRegistry.TryGet(constraintName, out var predicate))
                {
                    if (bound is null)
                    {
                        // Forwarded / unresolved CLR type — skip the
                        // built-in predicate check; precise enforcement
                        // happens at the next concrete instantiation.
                        continue;
                    }
                    satisfied = predicate(bound);
                    known = true;
                }
                else
                {
                    satisfied = TrySatisfyUserConstraint(constraintName, bound, argDisplay, out known);
                }

                if (satisfied) continue;
                if (!known) continue; // unknown name — accept conservatively

                throw new InvalidOperationException(
                    $"Generic class '{Name}' requires type parameter '{clause.TypeParameter}' to satisfy '{constraintName}', " +
                    $"but '{argDisplay}' (CLR {bound?.FullName ?? bound?.Name ?? "<unresolved>"}) does not.");
            }
        }
    }

    /// <summary>
    /// Tries to satisfy a non-built-in constraint name by treating it
    /// as a user-defined CLR interface, CLR class, or TōSh class /
    /// interface name. <paramref name="argDisplay"/> is the original
    /// user-supplied type-argument string (e.g. <c>"Dog"</c>) used
    /// to resolve TōSh-named user types whose CLR backing is shared
    /// across all user instances (so the CLR <see cref="Type"/>
    /// alone cannot identify the user class).
    /// </summary>
    /// <param name="known">
    /// Set to true when the name resolved to a known type (so a
    /// failure should produce a diagnostic). Left false when the
    /// name is unknown — those are accepted conservatively to keep
    /// custom constraint vocabularies extensible.
    /// </param>
    private bool TrySatisfyUserConstraint(string constraintName, Type? bound, string argDisplay, out bool known)
    {
        // CLR fallback: any registered CLR type whose
        // `IsAssignableFrom(bound)` holds satisfies the constraint.
        // This makes `where T: IDisposable` and similar work without
        // adding a built-in entry to the registry.
        if (bound is not null)
        {
            var clr = _engine.TryResolveTypeName(constraintName);
            if (clr is not null)
            {
                known = true;
                return clr.IsAssignableFrom(bound);
            }
        }

        // TōSh user-defined constraint. Only enforce when the
        // constraint name resolves to a `ToshInterfaceDefinition`
        // and the type-argument display name resolves to a
        // `ToshClassDefinition` whose interface chain (including
        // base classes) contains the constraint interface. Other
        // shell-named-type combinations (trait, struct, record …)
        // remain conservative for now — we only commit to the
        // interface case in this phase.
        if (_engine.TryGetNamedType(constraintName, out var constraintType)
            && constraintType is ToshInterfaceDefinition constraintIface)
        {
            var argLookup = StripGenericTypeArguments(argDisplay);
            if (_engine.TryGetNamedType(argLookup, out var argType))
            {
                if (argType is ToshClassDefinition argClass)
                {
                    known = true;
                    return ClassImplementsInterface(argClass, constraintIface.Name);
                }
                if (argType is ToshInterfaceDefinition argIface)
                {
                    // An interface type-arg satisfies an interface
                    // constraint when it is the same interface (we
                    // do not yet model interface inheritance).
                    known = true;
                    return string.Equals(argIface.Name, constraintIface.Name, StringComparison.OrdinalIgnoreCase);
                }
            }
            // Constraint is known, type-arg is not a recognised
            // TōSh class — fall through to conservative accept so
            // CLR-backed type args (e.g. `int`) do not trip the
            // diagnostic.
            known = false;
            return true;
        }

        // Constraint name resolves to some other shell-named type
        // (class, trait, etc.) — accept conservatively.
        if (_engine.TryGetNamedType(constraintName, out _))
        {
            known = false;
            return true;
        }

        known = false;
        return false;
    }

    private static string StripGenericTypeArguments(string typeName)
    {
        var lt = typeName.IndexOf('<');
        return lt < 0 ? typeName.Trim() : typeName.Substring(0, lt).Trim();
    }

    private static bool ClassImplementsInterface(ToshClassDefinition cls, string interfaceName)
    {
        var current = cls;
        while (current is not null)
        {
            foreach (var iface in current.ImplementedInterfaces)
            {
                if (string.Equals(iface.Name, interfaceName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            current = current.BaseClass;
        }
        return false;
    }

    private async Task<object> CreateInstanceCoreAsync(
        IReadOnlyList<object?> arguments,
        IReadOnlyDictionary<string, Type?>? typeArgumentBindings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (IsAbstract)
        {
            throw new InvalidOperationException($"Cannot create an instance of hollow class '{Name}'. Extend it with a concrete subclass first.");
        }

        if (IsHermit)
        {
            throw new InvalidOperationException($"Cannot create an instance of hermit class '{Name}'. Hermit classes contain only shared (static) members.");
        }

        if (TypeParameterNames.Count > 0 && typeArgumentBindings is null)
        {
            throw new InvalidOperationException(
                $"Generic class '{Name}' requires type arguments, e.g. " +
                $"'new {Name}<{string.Join(", ", TypeParameterNames)}>(…)'.");
        }

        var instance = new ToshClassInstance(this, typeArgumentBindings);
        await ConstructOnInstanceAsync(instance, arguments, cancellationToken);
        instance.CompleteInitialization();

        // Validate that all vital (required) properties have been set to non-null values
        foreach (var property in Properties.Where(p => p.IsVital && !p.IsStatic && !p.IsComputed))
        {
            if (instance.TryGetStoredValue(property.Name, out var value) && value is null)
            {
                throw new InvalidOperationException(
                    $"Vital property '{property.Name}' on class '{Name}' must be provided a value. " +
                    $"Set it in the constructor or provide an initializer.");
            }
        }

        return instance;
    }

    public InvocationResult InvokeStaticMethod(string methodName, IReadOnlyList<object?> arguments)
    {
        return InvokeStaticMethodAsync(methodName, arguments, CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();
    }

    public async ValueTask<InvocationResult> InvokeStaticMethodAsync(
        string methodName,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_methodsByName.TryGetValue(methodName, out var candidates))
        {
            throw new InvalidOperationException($"Static method '{methodName}' was not found on class '{Name}'.");
        }

        var staticCandidates = candidates.Where(candidate => candidate.IsStatic && !candidate.IsShy).ToArray();

        if (staticCandidates.Length == 0)
        {
            throw new InvalidOperationException($"'{methodName}' is an instance method on class '{Name}' and cannot be called statically. Create an instance first: var obj = new {Name}(); $obj.{methodName}(...)");
        }

        var (method, locals) = await SelectMethodAsync(
            staticCandidates,
            arguments,
            cancellationToken);
        var values = await ExecuteMethodBlockAsync(method, locals, instance: null, cancellationToken);
        return new InvocationResult(FlattenCallResult(values), ReturnedVoid: false);
    }

    public bool TryGetStaticMember(string memberName, out object? value)
    {
        value = null;

        // Check static properties
        if (_propertiesByName.TryGetValue(memberName, out var property) && property.IsStatic)
        {
            if (_staticValues.TryGetValue(memberName, out var stored))
            {
                value = stored;
                return true;
            }
            return true; // null default
        }

        if (_methodsByName.TryGetValue(memberName, out var candidates))
        {
            var isStatic = candidates.Any(c => c.IsStatic);
            var hint = isStatic
                ? $"'{memberName}' is a method on class '{Name}'. Call it with parentheses: {Name}.{memberName}(...)"
                : $"'{memberName}' is an instance method on class '{Name}'. Create an instance first: var obj = new {Name}(); $obj.{memberName}(...)";
            throw new InvalidOperationException(hint);
        }

        return false;
    }

    public bool TrySetStaticMember(string memberName, object? value)
    {
        if (_propertiesByName.TryGetValue(memberName, out var property) && property.IsStatic)
        {
            _staticValues[memberName] = value;
            return true;
        }

        return false;
    }

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        foreach (var field in GetMembers(includeHidden))
        {
            if (string.Equals(field.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = field.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    public bool TrySetMember(string name, object? value) => false;

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        return
        [
            new KeyValuePair<string, object?>("Name", ShellTypeName),
            new KeyValuePair<string, object?>("FullName", ShellFullName),
            new KeyValuePair<string, object?>("Namespace", ShellNamespace),
            new KeyValuePair<string, object?>("Assembly", ShellAssemblyName),
            new KeyValuePair<string, object?>("BaseType", ShellBaseTypeName),
            new KeyValuePair<string, object?>("IsClass", ShellIsClass),
            new KeyValuePair<string, object?>("IsInterface", ShellIsInterface),
            new KeyValuePair<string, object?>("IsEnum", ShellIsEnum),
            new KeyValuePair<string, object?>("IsValueType", ShellIsValueType),
            new KeyValuePair<string, object?>("IsAbstract", ShellIsAbstract),
            new KeyValuePair<string, object?>("IsSealed", IsSealed),
            new KeyValuePair<string, object?>("IsHermit", IsHermit),
            new KeyValuePair<string, object?>("IsStrict", IsStrict),
            new KeyValuePair<string, object?>("IsGenericType", ShellIsGenericType),
            new KeyValuePair<string, object?>("IsArray", ShellIsArray),
            new KeyValuePair<string, object?>("IsPublic", ShellIsPublic),
            new KeyValuePair<string, object?>("PropertyCount", GetShellMembers(includeHidden).Count(member => !member.IsStatic)),
            new KeyValuePair<string, object?>("StaticPropertyCount", GetShellMembers(includeHidden).Count(member => member.IsStatic)),
            new KeyValuePair<string, object?>("MethodCount", GetShellMethods(includeHidden).Count(method => !method.IsStatic)),
            new KeyValuePair<string, object?>("StaticMethodCount", GetShellMethods(includeHidden).Count(method => method.IsStatic)),
            new KeyValuePair<string, object?>("ConstructorCount", GetShellConstructors().Count),
        ];
    }

    public IReadOnlyList<ShellMemberDescriptor> GetShellMembers(bool includeHidden = false)
    {
        return Properties
            .Where(property => includeHidden || !property.IsShy)
            .Select(property => new ShellMemberDescriptor(
                property.Name,
                Kind: "Property",
                TypeName: GetAnnotationDisplayName(property.TypeName),
                IsStatic: property.IsStatic,
                IsWritable: property.IsWritable,
                IsHidden: property.IsShy))
            .ToArray();
    }

    public IReadOnlyList<ShellMethodDescriptor> GetShellMethods(bool includeHidden = false)
    {
        return Methods
            .Where(method => includeHidden || !method.IsShy)
            .OrderBy(method => method.Name, StringComparer.OrdinalIgnoreCase)
            .Select(method => new ShellMethodDescriptor(
                method.Name,
                ReturnTypeName: GetAnnotationDisplayName(method.ReturnTypeName),
                IsStatic: method.IsStatic,
                ParameterCount: method.Parameters.Count,
                Signature: FormatMethodSignature(method),
                IsHidden: method.IsShy))
            .ToArray();
    }

    public IReadOnlyList<ShellConstructorDescriptor> GetShellConstructors()
    {
        return GetConstructorMetadata()
            .DistinctBy(constructor => constructor.Signature, StringComparer.Ordinal)
            .ToArray();
    }

    internal bool TryGetInstanceMember(ToshClassInstance instance, string name, bool includeHidden, out object? value)
    {
        if (_propertiesByName.TryGetValue(name, out var property) &&
            !property.IsStatic &&
            (!property.IsShy || includeHidden) &&
            (!property.IsGuarded || includeHidden) &&
            (!property.IsLocal || includeHidden))
        {
            // Emit deprecation warning for fading properties
            if (property.IsFading)
            {
                _engine.WriteWarning(
                    code: "tosh.runtime.fading_member",
                    title: $"Property '{property.Name}' on class '{Name}' is fading (deprecated).",
                    help: "Use a non-fading replacement, or hush this code: hush tosh.runtime.fading_member",
                    category: Tosh.Runtime.ToshDiagnosticCategory.Deprecation);
            }

            if (property.GetterBody is not null)
            {
                value = EvaluatePropertyGetter(instance, property);
                return true;
            }

            // Lazy property: evaluate the initializer once, sharing the result
            // with concurrent readers while rejecting true recursive reads.
            if (property.IsLazy)
            {
                value = GetOrInitializeLazyProperty(instance, property);
                return true;
            }

            return instance.TryGetStoredValue(property.Name, out value);
        }

        if (BaseClass is not null)
        {
            return BaseClass.TryGetInstanceMember(instance, name, includeHidden, out value);
        }

        if (ClrBaseType is not null && instance.ClrBaseObject is not null)
        {
            try
            {
                value = _engine.Runtime.ObjectAccessor.GetValue(instance.ClrBaseObject, name);
                return true;
            }
            catch { /* member not found on CLR base */ }
        }

        value = null;
        return false;
    }

    internal async ValueTask<(bool Found, object? Value)> TryGetInstanceMemberAsync(
        ToshClassInstance instance,
        string name,
        bool includeHidden,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_propertiesByName.TryGetValue(name, out var property) &&
            !property.IsStatic &&
            (!property.IsShy || includeHidden) &&
            (!property.IsGuarded || includeHidden) &&
            (!property.IsLocal || includeHidden))
        {
            if (property.IsFading)
            {
                _engine.WriteWarning(
                    code: "tosh.runtime.fading_member",
                    title: $"Property '{property.Name}' on class '{Name}' is fading (deprecated).",
                    help: "Use a non-fading replacement, or hush this code: hush tosh.runtime.fading_member",
                    category: Tosh.Runtime.ToshDiagnosticCategory.Deprecation);
            }

            if (property.GetterBody is not null)
            {
                return (true, await EvaluatePropertyGetterAsync(instance, property, cancellationToken));
            }

            if (property.IsLazy)
            {
                return (true, await GetOrInitializeLazyPropertyAsync(
                    instance,
                    property,
                    cancellationToken));
            }

            return instance.TryGetStoredValue(property.Name, out var value)
                ? (true, value)
                : (false, null);
        }

        if (BaseClass is not null)
        {
            return await BaseClass.TryGetInstanceMemberAsync(
                instance,
                name,
                includeHidden,
                cancellationToken);
        }

        if (ClrBaseType is not null && instance.ClrBaseObject is not null)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return (true, _engine.Runtime.ObjectAccessor.GetValue(instance.ClrBaseObject, name));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Member not found on the CLR base.
            }
        }

        return (false, null);
    }

    private object? GetOrInitializeLazyProperty(
        ToshClassInstance instance,
        ToshClassPropertyDefinition property)
    {
        ThrowIfRecursiveLazyInitialization(instance, property);
        var initialization = instance.GetOrCreateLazyInitialization(property.Name);
        if (!initialization.IsOwner)
        {
            return initialization.Completion.GetAwaiter().GetResult();
        }

        var previous = instance.EnterLazyInitializationContext(property.Name);
        try
        {
            var value = GetInitialPropertyValue(
                instance,
                property,
                new Dictionary<string, object?>(StringComparer.Ordinal));
            instance.CompleteLazyInitialization(property.Name, value);
            return value;
        }
        catch (Exception exception)
        {
            instance.FailLazyInitialization(property.Name, exception);
            throw;
        }
        finally
        {
            instance.ExitLazyInitializationContext(previous);
        }
    }

    private async ValueTask<object?> GetOrInitializeLazyPropertyAsync(
        ToshClassInstance instance,
        ToshClassPropertyDefinition property,
        CancellationToken cancellationToken)
    {
        ThrowIfRecursiveLazyInitialization(instance, property);
        var initialization = instance.GetOrCreateLazyInitialization(property.Name);
        if (!initialization.IsOwner)
        {
            return await initialization.Completion.WaitAsync(cancellationToken);
        }

        var previous = instance.EnterLazyInitializationContext(property.Name);
        try
        {
            var value = await GetInitialPropertyValueAsync(
                instance,
                property,
                new Dictionary<string, object?>(StringComparer.Ordinal),
                cancellationToken);
            instance.CompleteLazyInitialization(property.Name, value);
            return value;
        }
        catch (Exception exception)
        {
            instance.FailLazyInitialization(property.Name, exception);
            throw;
        }
        finally
        {
            instance.ExitLazyInitializationContext(previous);
        }
    }

    private void ThrowIfRecursiveLazyInitialization(
        ToshClassInstance instance,
        ToshClassPropertyDefinition property)
    {
        if (instance.IsLazyInitializationActiveInCurrentContext(property.Name))
        {
            throw new InvalidOperationException(
                $"Lazy property '{property.Name}' on class '{Name}' recursively reads itself while initializing.");
        }
    }

    internal bool TrySetInstanceMember(ToshClassInstance instance, string name, object? value, bool includeHidden)
    {
        if (!_propertiesByName.TryGetValue(name, out var property) ||
            property.IsStatic ||
            (property.IsShy && !includeHidden) ||
            (property.IsGuarded && !includeHidden) ||
            (property.IsLocal && !includeHidden))
        {
            if (BaseClass is not null)
            {
                return BaseClass.TrySetInstanceMember(instance, name, value, includeHidden);
            }

            if (ClrBaseType is not null && instance.ClrBaseObject is not null)
            {
                try
                {
                    _engine.Runtime.ObjectAccessor.SetValue(instance.ClrBaseObject, name, value);
                    return true;
                }
                catch { /* member not found or read-only on CLR base */ }
            }

            return false;
        }

        if (property.SetterBody is not null)
        {
            ExecutePropertySetter(instance, property, value);
            return true;
        }

        if (property.GetterBody is not null)
        {
            throw new InvalidOperationException($"Property '{property.Name}' on class '{Name}' is read-only.");
        }

        if (property.IsFixed && !instance.IsInitializing)
        {
            throw new InvalidOperationException($"Property '{property.Name}' on class '{Name}' is fixed and cannot be reassigned after initialization.");
        }

        instance.SetStoredValue(property.Name, ConvertPropertyValue(instance, property, value));
        return true;
    }

    internal async ValueTask<bool> TrySetInstanceMemberAsync(
        ToshClassInstance instance,
        string name,
        object? value,
        bool includeHidden,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_propertiesByName.TryGetValue(name, out var property) ||
            property.IsStatic ||
            (property.IsShy && !includeHidden) ||
            (property.IsGuarded && !includeHidden) ||
            (property.IsLocal && !includeHidden))
        {
            if (BaseClass is not null)
            {
                return await BaseClass.TrySetInstanceMemberAsync(
                    instance,
                    name,
                    value,
                    includeHidden,
                    cancellationToken);
            }

            if (ClrBaseType is not null && instance.ClrBaseObject is not null)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _engine.Runtime.ObjectAccessor.SetValue(instance.ClrBaseObject, name, value);
                    return true;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Member not found or read-only on the CLR base.
                }
            }

            return false;
        }

        if (property.SetterBody is not null)
        {
            await ExecutePropertySetterAsync(instance, property, value, cancellationToken);
            return true;
        }

        if (property.GetterBody is not null)
        {
            throw new InvalidOperationException($"Property '{property.Name}' on class '{Name}' is read-only.");
        }

        if (property.IsFixed && !instance.IsInitializing)
        {
            throw new InvalidOperationException($"Property '{property.Name}' on class '{Name}' is fixed and cannot be reassigned after initialization.");
        }

        instance.SetStoredValue(
            property.Name,
            await ConvertPropertyValueAsync(
                instance,
                property,
                value,
                cancellationToken));
        return true;
    }

    internal IReadOnlyList<KeyValuePair<string, object?>> GetInstanceMembers(ToshClassInstance instance, bool includeHidden)
    {
        var members = new List<KeyValuePair<string, object?>>();

        // Include base class members first
        if (BaseClass is not null)
        {
            foreach (var baseMember in BaseClass.GetInstanceMembers(instance, includeHidden))
            {
                members.Add(baseMember);
            }
        }
        else if (ClrBaseType is not null && instance.ClrBaseObject is not null)
        {
            foreach (var prop in ClrBaseType.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                try { members.Add(new KeyValuePair<string, object?>(prop.Name, prop.GetValue(instance.ClrBaseObject))); }
                catch { members.Add(new KeyValuePair<string, object?>(prop.Name, null)); }
            }
        }

        foreach (var property in Properties)
        {
            if ((property.IsShy || property.IsGuarded) && !includeHidden)
            {
                continue;
            }

            // Skip if already provided by a base class (overridden)
            if (members.Any(m => string.Equals(m.Key, property.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            TryGetInstanceMember(instance, property.Name, includeHidden, out var value);
            members.Add(new KeyValuePair<string, object?>(property.Name, value));
        }

        return members;
    }

    internal async ValueTask<IReadOnlyList<KeyValuePair<string, object?>>> GetInstanceMembersAsync(
        ToshClassInstance instance,
        bool includeHidden,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var members = new List<KeyValuePair<string, object?>>();

        if (BaseClass is not null)
        {
            members.AddRange(await BaseClass.GetInstanceMembersAsync(
                instance,
                includeHidden,
                cancellationToken));
        }
        else if (ClrBaseType is not null && instance.ClrBaseObject is not null)
        {
            foreach (var property in ClrBaseType.GetProperties(
                         System.Reflection.BindingFlags.Public |
                         System.Reflection.BindingFlags.Instance))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    members.Add(new KeyValuePair<string, object?>(
                        property.Name,
                        property.GetValue(instance.ClrBaseObject)));
                }
                catch
                {
                    members.Add(new KeyValuePair<string, object?>(
                        property.Name,
                        null));
                }
            }
        }

        foreach (var property in Properties)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if ((property.IsShy || property.IsGuarded) && !includeHidden)
            {
                continue;
            }

            if (members.Any(member =>
                    string.Equals(
                        member.Key,
                        property.Name,
                        StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var lookup = await TryGetInstanceMemberAsync(
                instance,
                property.Name,
                includeHidden,
                cancellationToken);
            members.Add(new KeyValuePair<string, object?>(
                property.Name,
                lookup.Found ? lookup.Value : null));
        }

        return members;
    }

    internal void InvokeConstructorOnInstance(
        ToshClassInstance instance,
        IReadOnlyList<object?> arguments) =>
        InvokeConstructorOnInstanceAsync(instance, arguments, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    internal Task InvokeConstructorOnInstanceAsync(
        ToshClassInstance instance,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken) =>
        ConstructOnInstanceAsync(instance, arguments, cancellationToken);

    private async Task ConstructOnInstanceAsync(
        ToshClassInstance instance,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken,
        bool isImplicitBaseCall = false,
        ToshClassDefinition? requestedBy = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (instance.IsConstructionLayerComplete(this))
        {
            throw CreateConstructionDiagnostic(
                code: "tosh.runtime.base_constructor_already_initialized",
                title: $"Base class '{Name}' has already been initialized for this instance.",
                span: requestedBy?.Span ?? Span,
                label: "each class layer can be constructed only once",
                sourceName: requestedBy?.SourceName,
                sourceText: requestedBy?.SourceText);
        }

        if (!instance.TryBeginConstructionLayer(this))
        {
            throw CreateConstructionDiagnostic(
                code: "tosh.runtime.constructor_cycle",
                title: $"Constructor cycle detected while initializing class '{Name}'.",
                span: requestedBy?.Span ?? Span,
                label: "this class layer is already being initialized",
                sourceName: requestedBy?.SourceName,
                sourceText: requestedBy?.SourceText);
        }

        try
        {
            ToshClassConstructorDefinition constructor;
            Dictionary<string, object?> constructorLocals;

            try
            {
                (constructor, constructorLocals) = await SelectConstructorAsync(
                    arguments,
                    cancellationToken);
            }
            catch (InvalidOperationException) when (isImplicitBaseCall && requestedBy is not null)
            {
                throw requestedBy.CreateConstructionDiagnostic(
                    code: "tosh.runtime.missing_base_constructor_initializer",
                    title: $"Class '{requestedBy.Name}' must initialize base class '{Name}' with constructor arguments.",
                    span: requestedBy.Span,
                    label: $"add 'extends {Name}(...)' or a leading '$super(...)'",
                    help: $"'{Name}' has no constructor that can be called without arguments.");
            }

            ValidateConstructorTypeArguments(constructor, constructorLocals, instance);

            var (superInitializer, constructorBody) =
                SplitConstructorInitializer(constructor);

            if (BaseConstructorArgs is not null && superInitializer is not null)
            {
                throw CreateConstructionDiagnostic(
                    code: "tosh.runtime.duplicate_base_constructor_initializer",
                    title: $"Class '{Name}' initializes its base class more than once.",
                    span: superInitializer.Span,
                    label: "remove this '$super(...)' call or remove the 'extends Base(...)' arguments",
                    help: "Use exactly one base-constructor initializer.");
            }

            if (BaseClass is not null)
            {
                if (BaseConstructorArgs is not null)
                {
                    var baseArguments = await EvaluateBaseConstructorArgsAsync(
                        constructorLocals,
                        cancellationToken);
                    await BaseClass.ConstructOnInstanceAsync(
                        instance,
                        baseArguments,
                        cancellationToken);
                }
                else if (superInitializer is not null)
                {
                    await RunConstructorInitializerAsync(
                        instance,
                        constructor,
                        constructorLocals,
                        superInitializer,
                        cancellationToken);
                }
                else
                {
                    await BaseClass.ConstructOnInstanceAsync(
                        instance,
                        Array.Empty<object?>(),
                        cancellationToken,
                        isImplicitBaseCall: true,
                        requestedBy: this);
                }
            }
            else if (ClrBaseType is not null)
            {
                if (BaseConstructorArgs is not null)
                {
                    var baseArguments = await EvaluateBaseConstructorArgsAsync(
                        constructorLocals,
                        cancellationToken);
                    await InitializeClrBaseAsync(instance, baseArguments, cancellationToken);
                }
                else if (superInitializer is not null)
                {
                    await RunConstructorInitializerAsync(
                        instance,
                        constructor,
                        constructorLocals,
                        superInitializer,
                        cancellationToken);
                }
                else
                {
                    try
                    {
                        await InitializeClrBaseAsync(
                            instance,
                            Array.Empty<object?>(),
                            cancellationToken);
                    }
                    catch (Exception exception) when (
                        exception is not ToshDiagnosticException and not OperationCanceledException)
                    {
                        throw CreateConstructionDiagnostic(
                            code: "tosh.runtime.missing_base_constructor_initializer",
                            title: $"Class '{Name}' must initialize CLR base class '{ClrBaseType.FullName}' with constructor arguments.",
                            span: Span,
                            label: $"add 'extends {ClrBaseType.Name}(...)' or a leading '$super(...)'",
                            help: exception.Message);
                    }
                }
            }
            else if (superInitializer is not null)
            {
                throw CreateConstructionDiagnostic(
                    code: "tosh.runtime.super_without_base_class",
                    title: $"Class '{Name}' cannot call '$super(...)' because it has no base class.",
                    span: superInitializer.Span,
                    label: "remove this base-constructor initializer");
            }

            foreach (var property in Properties)
            {
                if (property.IsComputed || property.IsStatic || property.IsLazy || property.IsAbstract)
                {
                    continue;
                }

                var initialValue = await GetInitialPropertyValueAsync(
                    instance,
                    property,
                    constructorLocals,
                    cancellationToken);
                instance.SetStoredValue(property.Name, initialValue);
            }

            await RunConstructorAsync(
                instance,
                constructor with { Body = constructorBody },
                constructorLocals,
                cancellationToken);
            instance.CompleteConstructionLayer(this);
        }
        catch
        {
            instance.AbortConstructionLayer(this);
            throw;
        }
    }

    internal InvocationResult InvokeInstanceMethod(ToshClassInstance instance, string methodName, IReadOnlyList<object?> arguments, bool includeHidden)
    {
        return InvokeInstanceMethodAsync(
                instance,
                methodName,
                arguments,
                includeHidden,
                CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();
    }

    internal async ValueTask<InvocationResult> InvokeInstanceMethodAsync(
        ToshClassInstance instance,
        string methodName,
        IReadOnlyList<object?> arguments,
        bool includeHidden,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_methodsByName.TryGetValue(methodName, out var candidates))
        {
            // Allow calling the constructor by class name (e.g. $super.BaseClass(args))
            if (string.Equals(methodName, Name, StringComparison.OrdinalIgnoreCase))
            {
                await ConstructOnInstanceAsync(instance, arguments, cancellationToken);
                return new InvocationResult(null, ReturnedVoid: true);
            }

            if (BaseClass is not null)
            {
                return await BaseClass.InvokeInstanceMethodAsync(
                    instance,
                    methodName,
                    arguments,
                    includeHidden,
                    cancellationToken);
            }

            if (ClrBaseType is not null && instance.ClrBaseObject is not null)
            {
                return await _engine.Runtime.Invoker.InvokeInstanceMethodAsync(
                    instance.ClrBaseObject,
                    methodName,
                    arguments,
                    cancellationToken);
            }

            throw new InvalidOperationException($"Method '{methodName}' was not found on class '{Name}'.");
        }

        var (method, locals) = await SelectMethodAsync(
            candidates.Where(candidate => !candidate.IsStatic && (includeHidden || (!candidate.IsShy && !candidate.IsGuarded && !candidate.IsLocal))).ToArray(),
            arguments,
            cancellationToken,
            instance);

        // Emit deprecation warning for fading methods
        if (method.IsFading)
        {
            _engine.WriteWarning(
                code: "tosh.runtime.fading_member",
                title: $"Method '{method.Name}' on class '{Name}' is fading (deprecated).",
                help: "Use a non-fading replacement, or hush this code: hush tosh.runtime.fading_member",
                category: Tosh.Runtime.ToshDiagnosticCategory.Deprecation);
        }

        var values = await ExecuteMethodBlockAsync(method, locals, instance, cancellationToken);
        return new InvocationResult(FlattenCallResult(values), ReturnedVoid: false);
    }

    internal IEnumerable<object?> EnumerateItems(ToshClassInstance instance)
    {
        if (TryInvokeEnumerator(instance, out var value))
        {
            if (value is null)
            {
                yield break;
            }

            foreach (var item in ShellIterationUtilities.ExpandCollectionLikeValue(value))
            {
                yield return item;
            }

            yield break;
        }

        yield return instance;
    }

    internal bool HasEnumerator =>
        HasSpecialInstanceMethod("enumerate", Array.Empty<object?>()) ||
        HasSpecialInstanceMethod("GetEnumerator", Array.Empty<object?>());

    internal async IAsyncEnumerable<object?> EnumerateItemsAsync(
        ToshClassInstance instance,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        var enumeration = await TryInvokeEnumeratorAsync(instance, cancellationToken);
        if (enumeration.Matched)
        {
            if (enumeration.Value is null)
            {
                yield break;
            }

            foreach (var item in ShellIterationUtilities.ExpandCollectionLikeValue(enumeration.Value))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }

            yield break;
        }

        yield return instance;
    }

    internal bool HasSpecialInstanceMethod(string methodName, IReadOnlyList<object?> arguments)
    {
        return TrySelectSpecialInstanceMethod(methodName, arguments, out _, out _);
    }

    internal bool TryInvokeSpecialInstanceMethod(ToshClassInstance instance, string methodName, IReadOnlyList<object?> arguments, out object? value)
    {
        value = null;

        if (!TrySelectSpecialInstanceMethod(methodName, arguments, out var method, out var locals, instance))
        {
            return false;
        }

        var values = ExecuteMethodBlock(method, locals, instance);
        value = FlattenCallResult(values);
        return true;
    }

    internal async ValueTask<(bool Matched, object? Value)> TryInvokeSpecialInstanceMethodAsync(
        ToshClassInstance instance,
        string methodName,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var selection = await TrySelectSpecialInstanceMethodAsync(
            methodName,
            arguments,
            cancellationToken,
            instance);
        if (!selection.Matched)
        {
            return (false, null);
        }

        var values = await ExecuteMethodBlockAsync(
            selection.Method!,
            selection.Locals!,
            instance,
            cancellationToken);
        return (true, FlattenCallResult(values));
    }

    internal object? GetInitialPropertyValue(ToshClassInstance instance, ToshClassPropertyDefinition property, IReadOnlyDictionary<string, object?> constructorLocals)
    {
        if (property.Initializer is null)
        {
            return null;
        }

        var locals = CreateLocals(instance, constructorLocals);
        var value = _engine.EvaluateClassPipelineValueSync(SourceName, SourceText, property.Initializer, locals, CapturedScopes);
        return ConvertPropertyValue(instance, property, value);
    }

    internal async ValueTask<object?> GetInitialPropertyValueAsync(
        ToshClassInstance instance,
        ToshClassPropertyDefinition property,
        IReadOnlyDictionary<string, object?> constructorLocals,
        CancellationToken cancellationToken)
    {
        if (property.Initializer is null)
        {
            return null;
        }

        var locals = CreateLocals(instance, constructorLocals);
        var value = await _engine.EvaluateClassPipelineValueAsync(
            SourceName,
            SourceText,
            property.Initializer,
            locals,
            CapturedScopes,
            cancellationToken);
        return await ConvertPropertyValueAsync(
            instance,
            property,
            value,
            cancellationToken);
    }


    internal async Task RunConstructorAsync(
        ToshClassInstance instance,
        ToshClassConstructorDefinition constructor,
        IReadOnlyDictionary<string, object?> constructorLocals,
        CancellationToken cancellationToken)
    {
        var locals = CreateLocals(instance, constructorLocals);
        await _engine.ExecuteClassBlockAsync(
            constructor.SourceName,
            constructor.SourceText,
            constructor.Body,
            locals,
            constructor.CapturedScopes,
            $"{Name}()",
            cancellationToken);
    }

    private (PipelineStatementSyntax? Initializer, BlockSyntax Body)
        SplitConstructorInitializer(ToshClassConstructorDefinition constructor)
    {
        var initializerIndices = constructor.Body.Statements
            .Select((statement, index) => (statement, index))
            .Where(pair => IsDirectSuperConstructorCall(pair.statement))
            .ToArray();

        if (initializerIndices.Length > 1)
        {
            throw CreateConstructionDiagnostic(
                code: "tosh.runtime.duplicate_base_constructor_initializer",
                title: $"Constructor '{Name}()' calls '$super(...)' more than once.",
                span: initializerIndices[1].statement.Span,
                label: "remove this duplicate base-constructor initializer",
                sourceName: constructor.SourceName,
                sourceText: constructor.SourceText);
        }

        if (initializerIndices.Length == 0)
        {
            return (null, constructor.Body);
        }

        var (statement, index) = initializerIndices[0];
        if (index != 0)
        {
            throw CreateConstructionDiagnostic(
                code: "tosh.runtime.super_initializer_must_be_first",
                title: "'$super(...)' must be the first executable statement in a constructor.",
                span: statement.Span,
                label: "move this call to the start of the constructor",
                sourceName: constructor.SourceName,
                sourceText: constructor.SourceText);
        }

        var initializer = (PipelineStatementSyntax)statement;
        var body = new BlockSyntax(
            constructor.Body.Statements.Skip(1).ToArray(),
            constructor.Body.Span);
        return (initializer, body);
    }

    private static bool IsDirectSuperConstructorCall(StatementSyntax statement)
    {
        if (statement is not PipelineStatementSyntax
            {
                Pipeline:
                {
                    Stages:
                    [
                        ExpressionPipelineStageSyntax
                        {
                            Expression: CallableInvocationArgumentSyntax
                            {
                                Target: VariableReferenceArgumentSyntax { Name: var name },
                            },
                        },
                    ],
                    IsBackground: false,
                },
            } pipelineStatement)
        {
            return false;
        }

        if (pipelineStatement.Pipeline.Redirections is { Count: > 0 }
            || pipelineStatement.Pipeline.InputRedirection is not null)
        {
            return false;
        }

        return string.Equals(name, "super", StringComparison.OrdinalIgnoreCase);
    }


    private async Task RunConstructorInitializerAsync(
        ToshClassInstance instance,
        ToshClassConstructorDefinition constructor,
        IReadOnlyDictionary<string, object?> constructorLocals,
        PipelineStatementSyntax initializer,
        CancellationToken cancellationToken)
    {
        var locals = CreateLocals(instance, constructorLocals);
        var block = new BlockSyntax([initializer], initializer.Span);
        await _engine.ExecuteClassBlockAsync(
            constructor.SourceName,
            constructor.SourceText,
            block,
            locals,
            constructor.CapturedScopes,
            $"{Name}.base()",
            cancellationToken);
    }


    private async Task InitializeClrBaseAsync(
        ToshClassInstance instance,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken)
    {
        var clrObject = await _engine.Runtime.Invoker.CreateInstanceAsync(
            ClrBaseType!,
            arguments,
            cancellationToken);
        if (instance.TryInitializeClrBase(clrObject))
        {
            return;
        }

        throw CreateConstructionDiagnostic(
            code: "tosh.runtime.base_constructor_already_initialized",
            title: $"CLR base class '{ClrBaseType!.FullName}' has already been initialized for this instance.",
            span: Span,
            label: "each base class can be constructed only once");
    }

    private ToshDiagnosticException CreateConstructionDiagnostic(
        string code,
        string title,
        TextSpan span,
        string label,
        string? help = null,
        string? sourceName = null,
        string? sourceText = null) =>
        ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: code,
            Title: title,
            SourceName: sourceName ?? SourceName,
            SourceText: sourceText ?? SourceText,
            Span: span,
            Label: label,
            Help: help));


    internal async Task<IReadOnlyList<object?>> EvaluateBaseConstructorArgsAsync(
        IReadOnlyDictionary<string, object?> constructorLocals,
        CancellationToken cancellationToken)
    {
        if (BaseConstructorArgs is null or { Count: 0 })
        {
            return Array.Empty<object?>();
        }

        var args = new List<object?>();
        foreach (var argPipeline in BaseConstructorArgs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = await _engine.EvaluateClassPipelineValueAsync(
                SourceName,
                SourceText,
                argPipeline,
                constructorLocals,
                CapturedScopes,
                cancellationToken);
            args.Add(value);
        }

        return args;
    }

    private object? EvaluatePropertyGetter(ToshClassInstance instance, ToshClassPropertyDefinition property)
    {
        var locals = CreateLocals(instance, new Dictionary<string, object?>(StringComparer.Ordinal));
        var values = _engine.ExecuteClassBlockSync(SourceName, SourceText, property.GetterBody!, locals, CapturedScopes, $"{Name}.{property.Name}.get");
        return FlattenCallResult(values);
    }

    private async ValueTask<object?> EvaluatePropertyGetterAsync(
        ToshClassInstance instance,
        ToshClassPropertyDefinition property,
        CancellationToken cancellationToken)
    {
        var locals = CreateLocals(instance, new Dictionary<string, object?>(StringComparer.Ordinal));
        var values = await _engine.ExecuteClassBlockAsync(
            SourceName,
            SourceText,
            property.GetterBody!,
            locals,
            CapturedScopes,
            $"{Name}.{property.Name}.get",
            cancellationToken);
        return FlattenCallResult(values);
    }

    private void ExecutePropertySetter(ToshClassInstance instance, ToshClassPropertyDefinition property, object? value)
    {
        var locals = CreateLocals(instance, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["value"] = value,
        });
        _engine.ExecuteClassBlockSync(SourceName, SourceText, property.SetterBody!, locals, CapturedScopes, $"{Name}.{property.Name}.set");
    }

    private async ValueTask ExecutePropertySetterAsync(
        ToshClassInstance instance,
        ToshClassPropertyDefinition property,
        object? value,
        CancellationToken cancellationToken)
    {
        var locals = CreateLocals(instance, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["value"] = value,
        });
        await _engine.ExecuteClassBlockAsync(
            SourceName,
            SourceText,
            property.SetterBody!,
            locals,
            CapturedScopes,
            $"{Name}.{property.Name}.set",
            cancellationToken);
    }

    private object? ConvertPropertyValue(ToshClassInstance? instance, ToshClassPropertyDefinition property, object? value)
    {
        if (property.TypeName is null)
        {
            return value;
        }

        // Substitute generic type-parameter names (e.g. 'T1') against the
        // instance's resolved type-argument bindings. When the property's
        // declared type is itself a class type-parameter we apply a
        // strict no-coercion check (matching the constructor and method
        // parameter behaviour); otherwise we go through the engine's
        // standard annotated-value conversion path.
        if (instance is not null)
        {
            var bindings = instance.GetBindingsFor(this);
            if (bindings is not null && bindings.TryGetValue(property.TypeName, out var boundType))
            {
                if (boundType is null)
                {
                    // Type parameter is recognised but the user did not
                    // supply a resolvable CLR type — accept any value
                    // (effectively nominal-only).
                    return value;
                }

                EnforceStrictBinding(
                    boundType,
                    value,
                    property.Span,
                    SourceName,
                    SourceText,
                    $"{Name}.{property.Name}");
                return value;
            }
        }

        return _engine.ConvertAnnotatedValue(
            property.TypeName,
            property.Refinement,
            value,
            property.Span,
            SourceName,
            SourceText,
            $"{Name}.{property.Name}");
    }

    private async ValueTask<object?> ConvertPropertyValueAsync(
        ToshClassInstance? instance,
        ToshClassPropertyDefinition property,
        object? value,
        CancellationToken cancellationToken)
    {
        if (property.TypeName is null)
        {
            return value;
        }

        if (instance is not null)
        {
            var bindings = instance.GetBindingsFor(this);
            if (bindings is not null &&
                bindings.TryGetValue(property.TypeName, out var boundType))
            {
                if (boundType is null)
                {
                    return value;
                }

                EnforceStrictBinding(
                    boundType,
                    value,
                    property.Span,
                    SourceName,
                    SourceText,
                    $"{Name}.{property.Name}");
                return value;
            }
        }

        return await _engine.ConvertAnnotatedValueAsync(
            property.TypeName,
            property.Refinement,
            value,
            property.Span,
            SourceName,
            SourceText,
            $"{Name}.{property.Name}",
            cancellationToken);
    }

    private IReadOnlyList<object?> ExecuteMethodBlock(ToshClassMethodDefinition method, IReadOnlyDictionary<string, object?> boundLocals, ToshClassInstance? instance)
    {
        return ExecuteMethodBlockAsync(method, boundLocals, instance, CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();
    }

    private async ValueTask<IReadOnlyList<object?>> ExecuteMethodBlockAsync(
        ToshClassMethodDefinition method,
        IReadOnlyDictionary<string, object?> boundLocals,
        ToshClassInstance? instance,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Phase 3.4 — method-level generic inference.
        // For methods that declare their own type parameters
        // (`func describe<U>(label: U) -> U`), unify each argument
        // value against the parameter's raw annotation to populate a
        // method-scoped binding table. These bindings are merged with
        // any class-level bindings carried by the instance so that
        // `class Box<T>` + `func map<U>(transform)` both resolve.
        Dictionary<string, Type>? methodBindings = null;
        if (method.TypeParameters is { Count: > 0 })
        {
            var argumentValues = new object?[method.Parameters.Count];
            var argumentSpans = new TextSpan[method.Parameters.Count];
            for (var i = 0; i < method.Parameters.Count; i++)
            {
                var parameter = method.Parameters[i];
                argumentSpans[i] = parameter.Span;
                boundLocals.TryGetValue(parameter.Name, out var v);
                argumentValues[i] = v;
            }

            var syntheticInvocation = new CommandInvocation(
                SourceName: method.SourceName,
                SourceText: method.SourceText,
                CommandName: $"{Name}.{method.Name}",
                CommandSpan: method.Span,
                ArgumentSpans: argumentSpans);
            var syntheticContext = new CommandContext(
                Runtime: _engine.Runtime,
                Input: System.Linq.AsyncEnumerable.Empty<object?>(),
                Arguments: argumentValues,
                CancellationToken: cancellationToken,
                Invocation: syntheticInvocation);

            methodBindings = _engine.InferMethodTypeBindings(
                method,
                argumentValues,
                syntheticContext,
                ownerLabel: $"{Name}.{method.Name}");
        }

        // For generic instance methods, validate any parameters whose original
        // (un-erased) annotation references a class type-parameter and
        // substitute the instance's binding before running the body.
        if (instance is not null && !method.IsStatic)
        {
            var bindings = instance.GetBindingsFor(this);
            if (bindings is { Count: > 0 } || methodBindings is { Count: > 0 })
            {
                foreach (var parameter in method.Parameters)
                {
                    if (parameter.RawTypeName is null) continue;
                    Type? bound = null;
                    if (bindings is not null && bindings.TryGetValue(parameter.RawTypeName, out var classBound)) bound = classBound;
                    if (bound is null && methodBindings is not null && methodBindings.TryGetValue(parameter.RawTypeName, out var mBound)) bound = mBound;
                    if (bound is null) continue;
                    if (!boundLocals.TryGetValue(parameter.Name, out var value)) continue;

                    if (parameter.IsRest && value is System.Collections.IList list)
                    {
                        for (int i = 0; i < list.Count; i++)
                        {
                            EnforceStrictBinding(
                                bound,
                                list[i],
                                parameter.Span,
                                method.SourceName,
                                method.SourceText,
                                $"{Name}.{method.Name}.{parameter.Name}");
                        }
                    }
                    else
                    {
                        EnforceStrictBinding(
                            bound,
                            value,
                            parameter.Span,
                            method.SourceName,
                            method.SourceText,
                            $"{Name}.{method.Name}.{parameter.Name}");
                    }
                }
            }
        }
        else if (methodBindings is { Count: > 0 })
        {
            // Static method (or no instance) — apply method-level bindings only.
            foreach (var parameter in method.Parameters)
            {
                if (parameter.RawTypeName is null) continue;
                if (!methodBindings.TryGetValue(parameter.RawTypeName, out var bound) || bound is null) continue;
                if (!boundLocals.TryGetValue(parameter.Name, out var value)) continue;
                if (parameter.IsRest && value is System.Collections.IList list)
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        EnforceStrictBinding(bound, list[i], parameter.Span, method.SourceName, method.SourceText, $"{Name}.{method.Name}.{parameter.Name}");
                    }
                }
                else
                {
                    EnforceStrictBinding(bound, value, parameter.Span, method.SourceName, method.SourceText, $"{Name}.{method.Name}.{parameter.Name}");
                }
            }
        }

        var locals = CreateLocals(instance, boundLocals);
        var values = await _engine.ExecuteClassBlockAsync(
            method.SourceName,
            method.SourceText,
            method.Body,
            locals,
            method.CapturedScopes,
            $"{Name}.{method.Name}",
            cancellationToken);

        // Resolve the effective return-type annotation: prefer the un-erased
        // RawReturnTypeName when it names a bound class type-parameter,
        // otherwise fall through to the (possibly-erased) ReturnTypeName.
        // When the return type is bound from a type-parameter we apply a
        // strict no-coercion check; otherwise we go through the engine's
        // standard annotated-value conversion path.
        Type? strictReturnBinding = null;
        string? effectiveReturnType = method.ReturnTypeName;
        if (method.RawReturnTypeName is not null)
        {
            // Prefer instance (class-level) binding, then method-level.
            if (instance is not null)
            {
                var bindings = instance.GetBindingsFor(this);
                if (bindings is not null && bindings.TryGetValue(method.RawReturnTypeName, out var bound))
                {
                    if (bound is null)
                    {
                        effectiveReturnType = null;
                    }
                    else
                    {
                        strictReturnBinding = bound;
                        effectiveReturnType = bound.FullName ?? bound.Name;
                    }
                }
            }
            if (strictReturnBinding is null && effectiveReturnType is not null && methodBindings is not null
                && methodBindings.TryGetValue(method.RawReturnTypeName, out var mBound) && mBound is not null)
            {
                strictReturnBinding = mBound;
                effectiveReturnType = mBound.FullName ?? mBound.Name;
            }
        }

        if (strictReturnBinding is not null)
        {
            var unwrapped = UnwrapValues(values).ToArray();
            foreach (var value in unwrapped)
            {
                EnforceStrictBinding(
                    strictReturnBinding,
                    value,
                    method.Span,
                    method.SourceName,
                    method.SourceText,
                    $"{Name}.{method.Name}");
            }
            return unwrapped;
        }

        var returnValues = UnwrapValues(values);
        if (effectiveReturnType is null)
        {
            return returnValues;
        }

        var convertedReturnValues = new object?[returnValues.Count];
        for (var index = 0; index < returnValues.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            convertedReturnValues[index] = await _engine.ConvertAnnotatedValueAsync(
                effectiveReturnType,
                returnValues[index],
                method.Span,
                method.SourceName,
                method.SourceText,
                $"{Name}.{method.Name}",
                cancellationToken);
        }

        return convertedReturnValues;
    }

    private IReadOnlyDictionary<string, object?> CreateLocals(ToshClassInstance? instance, IReadOnlyDictionary<string, object?> locals)
    {
        var result = new Dictionary<string, object?>(locals, StringComparer.Ordinal)
        {
            ["args"] = locals.Values.ToArray(),
        };

        if (instance is not null)
        {
            result["this"] = new ToshClassSelfReference(instance);

            if (BaseClass is not null)
            {
                result["super"] = new ToshClassSuperReference(instance, BaseClass);
            }
            else if (ClrBaseType is not null)
            {
                result["super"] = new ToshClassClrSuperReference(instance, ClrBaseType, _engine);
            }
        }

        return result;
    }

    /// <summary>
    /// The `this`/`super` bindings an instance member sees. Used to make
    /// them visible inside method parameter defaults (TS-P1-21).
    /// Returns null for static members and for constructors, where the
    /// instance is not yet initialised.
    /// </summary>
    private IReadOnlyDictionary<string, object?>? CreateSelfBindings(ToshClassInstance? instance)
    {
        if (instance is null)
        {
            return null;
        }

        var bindings = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["this"] = new ToshClassSelfReference(instance),
        };

        if (BaseClass is not null)
        {
            bindings["super"] = new ToshClassSuperReference(instance, BaseClass);
        }
        else if (ClrBaseType is not null)
        {
            bindings["super"] = new ToshClassClrSuperReference(instance, ClrBaseType, _engine);
        }

        return bindings;
    }


    private async ValueTask<(
        ToshClassConstructorDefinition Constructor,
        Dictionary<string, object?> Locals)> SelectConstructorAsync(
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken)
    {
        var constructors = GetConstructorDefinitions();
        var matches = await _engine.SelectBestCallableMatchesAsync(
            constructors,
            static candidate => candidate.Parameters,
            arguments,
            cancellationToken);

        if (matches.Count == 0)
        {
            if (constructors.Count == 1)
            {
                await ThrowDetailedSingleConstructorMismatchAsync(
                    constructors[0],
                    arguments,
                    cancellationToken);
            }

            throw new InvalidOperationException($"No constructor matched class '{Name}' with {arguments.Count} argument(s).");
        }

        if (matches.Count > 1)
        {
            var signatures = string.Join(
                "; ",
                matches.Select(match => FormatConstructorSignature(match.Candidate.Parameters)));
            throw new InvalidOperationException(
                $"Multiple constructor overloads matched class '{Name}' with {arguments.Count} argument(s): {signatures}.");
        }

        var winner = matches[0].Candidate;
        var locals = matches[0].Locals;
        await _engine.ApplyPendingParameterDefaultsAsync(
            winner.Parameters,
            locals,
            matches[0].PendingDefaults,
            winner.SourceName,
            winner.SourceText,
            winner.CapturedScopes,
            $"{Name}()",
            cancellationToken,
            ambient: null,
            selfUnavailable: true);
        return (winner, locals);
    }

    private void ThrowDetailedSingleConstructorMismatch(ToshClassConstructorDefinition constructor, IReadOnlyList<object?> arguments)
    {
        var parameters = constructor.Parameters;
        var hasRestParameter = parameters.Count > 0 && parameters[^1].IsRest;
        var positionalCount = hasRestParameter ? parameters.Count - 1 : parameters.Count;

        var namedArgs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var positionalArgs = new List<object?>();

        foreach (var arg in arguments)
        {
            if (arg is NamedArgument named)
            {
                namedArgs[named.Name] = named.Value;
            }
            else
            {
                positionalArgs.Add(arg);
            }
        }

        var requiredCount = parameters.Count(parameter =>
            !parameter.IsOptional && !parameter.IsRest && parameter.DefaultValue is null && !namedArgs.ContainsKey(parameter.Name));

        if (positionalArgs.Count < requiredCount || (!hasRestParameter && positionalArgs.Count > positionalCount - namedArgs.Count))
        {
            return;
        }

        var positionalIndex = 0;
        for (var index = 0; index < positionalCount; index++)
        {
            var parameter = parameters[index];

            if (namedArgs.TryGetValue(parameter.Name, out var namedValue))
            {
                ConvertConstructorParameterValue(constructor, parameter, namedValue);
                continue;
            }

            if (positionalIndex >= positionalArgs.Count)
            {
                continue;
            }

            ConvertConstructorParameterValue(constructor, parameter, positionalArgs[positionalIndex++]);
        }

        if (hasRestParameter)
        {
            var restParam = parameters[^1];
            for (var index = positionalCount; index < arguments.Count; index++)
            {
                ConvertConstructorParameterValue(constructor, restParam, arguments[index]);
            }
        }
    }

    private async Task ThrowDetailedSingleConstructorMismatchAsync(
        ToshClassConstructorDefinition constructor,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken)
    {
        var parameters = constructor.Parameters;
        var hasRestParameter = parameters.Count > 0 && parameters[^1].IsRest;
        var positionalCount = hasRestParameter ? parameters.Count - 1 : parameters.Count;
        var namedArgs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var positionalArgs = new List<object?>();

        foreach (var argument in arguments)
        {
            if (argument is NamedArgument named)
            {
                namedArgs[named.Name] = named.Value;
            }
            else
            {
                positionalArgs.Add(argument);
            }
        }

        var requiredCount = parameters.Count(parameter =>
            !parameter.IsOptional &&
            !parameter.IsRest &&
            parameter.DefaultValue is null &&
            !namedArgs.ContainsKey(parameter.Name));

        if (positionalArgs.Count < requiredCount ||
            (!hasRestParameter && positionalArgs.Count > positionalCount - namedArgs.Count))
        {
            return;
        }

        var positionalIndex = 0;
        for (var index = 0; index < positionalCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parameter = parameters[index];

            if (namedArgs.TryGetValue(parameter.Name, out var namedValue))
            {
                await ConvertConstructorParameterValueAsync(
                    constructor,
                    parameter,
                    namedValue,
                    cancellationToken);
                continue;
            }

            if (positionalIndex >= positionalArgs.Count)
            {
                continue;
            }

            await ConvertConstructorParameterValueAsync(
                constructor,
                parameter,
                positionalArgs[positionalIndex++],
                cancellationToken);
        }

        if (hasRestParameter)
        {
            var restParameter = parameters[^1];
            for (var index = positionalCount; index < arguments.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ConvertConstructorParameterValueAsync(
                    constructor,
                    restParameter,
                    arguments[index],
                    cancellationToken);
            }
        }
    }

    private object? ConvertConstructorParameterValue(
        ToshClassConstructorDefinition constructor,
        FunctionParameterDefinition parameter,
        object? value)
    {
        try
        {
            return _engine.ConvertAnnotatedValue(
                parameter.TypeName,
                parameter.Refinement,
                value,
                parameter.Span,
                constructor.SourceName,
                constructor.SourceText,
                $"{Name}.{parameter.Name}");
        }
        catch (ToshDiagnosticException exception)
        {
            if (exception.Diagnostics.Any(diagnostic =>
                string.Equals(diagnostic.Code, "tosh.runtime.annotation_unknown_type", StringComparison.Ordinal) ||
                string.Equals(diagnostic.Code, "tosh.runtime.refinement_failed", StringComparison.Ordinal) ||
                string.Equals(diagnostic.Code, "tosh.runtime.expression_failed", StringComparison.Ordinal)))
            {
                throw;
            }

            if (parameter.Refinement is not null)
            {
                throw;
            }

            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.constructor_parameter_type_conversion_failed",
                Title: $"Constructor argument '{parameter.Name}' could not be converted to '{parameter.TypeName}'.",
                SourceName: constructor.SourceName,
                SourceText: constructor.SourceText,
                Span: parameter.Span,
                Label: $"'{parameter.Name}' expects {parameter.TypeName}"));
        }
    }

    private async ValueTask<object?> ConvertConstructorParameterValueAsync(
        ToshClassConstructorDefinition constructor,
        FunctionParameterDefinition parameter,
        object? value,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _engine.ConvertAnnotatedValueAsync(
                parameter.TypeName,
                parameter.Refinement,
                value,
                parameter.Span,
                constructor.SourceName,
                constructor.SourceText,
                $"{Name}.{parameter.Name}",
                cancellationToken);
        }
        catch (ToshDiagnosticException exception)
        {
            if (exception.Diagnostics.Any(diagnostic =>
                    string.Equals(diagnostic.Code, "tosh.runtime.annotation_unknown_type", StringComparison.Ordinal) ||
                    string.Equals(diagnostic.Code, "tosh.runtime.refinement_failed", StringComparison.Ordinal) ||
                    string.Equals(diagnostic.Code, "tosh.runtime.expression_failed", StringComparison.Ordinal)))
            {
                throw;
            }

            if (parameter.Refinement is not null)
            {
                throw;
            }

            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.constructor_parameter_type_conversion_failed",
                Title: $"Constructor argument '{parameter.Name}' could not be converted to '{parameter.TypeName}'.",
                SourceName: constructor.SourceName,
                SourceText: constructor.SourceText,
                Span: parameter.Span,
                Label: $"'{parameter.Name}' expects {parameter.TypeName}"));
        }
    }

    /// <summary>
    /// After a constructor has been selected, walk its parameters and for any
    /// whose original (un-erased) annotation referred to a class type-parameter,
    /// substitute the bound CLR type and validate the supplied argument. Mutates
    /// <paramref name="locals"/> in place to reflect any coercion that
    /// <see cref="ToshEngine.ConvertAnnotatedValue(string, RefinementAnnotation?, object?, TextSpan, string, string, string)"/>
    /// performed.
    /// </summary>
    private void ValidateConstructorTypeArguments(
        ToshClassConstructorDefinition constructor,
        Dictionary<string, object?> locals,
        ToshClassInstance instance)
    {
        var bindings = instance.GetBindingsFor(this);
        if (bindings is null || bindings.Count == 0)
        {
            return;
        }

        foreach (var parameter in constructor.Parameters)
        {
            if (parameter.RawTypeName is null)
            {
                continue;
            }

            // Only revalidate parameters whose RawTypeName is actually a
            // class type-parameter (otherwise the standard conversion in
            // SelectBestCallableMatches has already enforced the annotation).
            if (!bindings.TryGetValue(parameter.RawTypeName, out var boundType))
            {
                continue;
            }

            if (boundType is null)
            {
                // Unresolved type-parameter binding — accept any value.
                continue;
            }

            if (!locals.TryGetValue(parameter.Name, out var value))
            {
                continue;
            }

            if (parameter.IsRest && value is System.Collections.IList list)
            {
                for (var i = 0; i < list.Count; i++)
                {
                    EnforceStrictBinding(
                        boundType,
                        list[i],
                        parameter.Span,
                        constructor.SourceName,
                        constructor.SourceText,
                        $"{Name}.{parameter.Name}");
                }
                continue;
            }

            EnforceStrictBinding(
                boundType,
                value,
                parameter.Span,
                constructor.SourceName,
                constructor.SourceText,
                $"{Name}.{parameter.Name}");
        }
    }

    /// <summary>
    /// Strict (no-coercion) check used when a parameter or return type was
    /// declared as a class type-parameter (e.g. <c>x: T</c>) and is being
    /// re-validated against the concrete CLR type bound at instantiation
    /// (e.g. <c>T = int</c>). Unlike
    /// <see cref="ToshEngine.ConvertAnnotatedValue(string, RefinementAnnotation?, object?, TextSpan, string, string, string)"/>
    /// this does not run <see cref="TypeConversion"/>; it only accepts a
    /// value that is already an instance of the bound type. This prevents
    /// surprising widening (e.g. <c>int</c> → <c>double</c>) and
    /// stringification (e.g. <c>4</c> → <c>"4"</c>) that would
    /// otherwise silently succeed for <c>new Box&lt;string&gt;(4)</c>.
    /// </summary>
    private void EnforceStrictBinding(
        Type boundType,
        object? value,
        TextSpan span,
        string sourceName,
        string sourceText,
        string owner)
    {
        if (value is null)
        {
            if (!boundType.IsValueType || Nullable.GetUnderlyingType(boundType) is not null)
            {
                return;
            }
        }
        else if (boundType.IsInstanceOfType(value))
        {
            return;
        }

        var typeName = boundType.FullName ?? boundType.Name;
        throw ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.runtime.annotation_conversion_failed",
            Title: $"'{owner}' produced a value that could not be converted to '{typeName}'.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: span,
            Label: $"the value does not match '{typeName}'"));
    }


    private async ValueTask<(
        ToshClassMethodDefinition Method,
        Dictionary<string, object?> Locals)> SelectMethodAsync(
        IReadOnlyList<ToshClassMethodDefinition> candidates,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken,
        ToshClassInstance? instance = null)
    {
        var matches = await _engine.SelectBestCallableMatchesAsync(
            candidates,
            static candidate => candidate.Parameters,
            arguments,
            cancellationToken);

        if (matches.Count == 0)
        {
            var methodDisplayName = candidates.Count > 0
                ? $"'{Name}.{candidates[0].Name}'"
                : $"'{Name}'";
            throw new InvalidOperationException(
                $"No overload matched {methodDisplayName} with {arguments.Count} argument(s).");
        }

        if (matches.Count > 1)
        {
            var methodDisplayName = candidates.Count > 0
                ? $"{Name}.{candidates[0].Name}"
                : Name;
            var signatures = string.Join(
                "; ",
                matches.Select(match => FormatMethodSignature(match.Candidate)));
            throw new InvalidOperationException(
                $"Multiple overloads matched method '{methodDisplayName}' with {arguments.Count} argument(s): {signatures}.");
        }

        var winner = matches[0].Candidate;
        var locals = matches[0].Locals;
        await _engine.ApplyPendingParameterDefaultsAsync(
            winner.Parameters,
            locals,
            matches[0].PendingDefaults,
            winner.SourceName,
            winner.SourceText,
            winner.CapturedScopes,
            $"{Name}.{winner.Name}",
            cancellationToken,
            ambient: CreateSelfBindings(instance));
        return (winner, locals);
    }

    private bool TryInvokeEnumerator(ToshClassInstance instance, out object? value)
    {
        foreach (var methodName in new[] { "enumerate", "GetEnumerator" })
        {
            if (!TryInvokeSpecialInstanceMethod(instance, methodName, Array.Empty<object?>(), out value))
            {
                continue;
            }

            return true;
        }

        value = null;
        return false;
    }

    private async ValueTask<(bool Matched, object? Value)> TryInvokeEnumeratorAsync(
        ToshClassInstance instance,
        CancellationToken cancellationToken)
    {
        foreach (var methodName in new[] { "enumerate", "GetEnumerator" })
        {
            var invocation = await TryInvokeSpecialInstanceMethodAsync(
                instance,
                methodName,
                Array.Empty<object?>(),
                cancellationToken);
            if (invocation.Matched)
            {
                return invocation;
            }
        }

        return (false, null);
    }

    private bool TrySelectSpecialInstanceMethod(
        string methodName,
        IReadOnlyList<object?> arguments,
        out ToshClassMethodDefinition method,
        out Dictionary<string, object?> locals,
        ToshClassInstance? instance = null)
    {
        if (!_methodsByName.TryGetValue(methodName, out var candidates))
        {
            if (BaseClass is not null)
            {
                return BaseClass.TrySelectSpecialInstanceMethod(methodName, arguments, out method, out locals, instance);
            }

            method = null!;
            locals = null!;
            return false;
        }

        var matches = _engine.SelectBestCallableMatches(
            candidates.Where(candidate => !candidate.IsStatic),
            static candidate => candidate.Parameters,
            arguments);

        if (matches.Count == 0)
        {
            method = null!;
            locals = null!;
            return false;
        }

        if (matches.Count > 1)
        {
            var signatures = string.Join(
                "; ",
                matches.Select(match => FormatMethodSignature(match.Candidate)));
            throw new InvalidOperationException(
                $"Multiple overloads matched special method '{Name}.{methodName}' with {arguments.Count} argument(s): {signatures}.");
        }

        method = matches[0].Candidate;
        locals = matches[0].Locals;
        _engine.ApplyPendingParameterDefaults(
            method.Parameters,
            locals,
            matches[0].PendingDefaults,
            method.SourceName,
            method.SourceText,
            method.CapturedScopes,
            $"{Name}.{method.Name}",
            ambient: CreateSelfBindings(instance));
        return true;
    }

    private async ValueTask<(
        bool Matched,
        ToshClassMethodDefinition? Method,
        Dictionary<string, object?>? Locals)> TrySelectSpecialInstanceMethodAsync(
        string methodName,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken,
        ToshClassInstance? instance = null)
    {
        if (!_methodsByName.TryGetValue(methodName, out var candidates))
        {
            if (BaseClass is not null)
            {
                return await BaseClass.TrySelectSpecialInstanceMethodAsync(
                    methodName,
                    arguments,
                    cancellationToken,
                    instance);
            }

            return (false, null, null);
        }

        var matches = await _engine.SelectBestCallableMatchesAsync(
            candidates.Where(candidate => !candidate.IsStatic),
            static candidate => candidate.Parameters,
            arguments,
            cancellationToken);

        if (matches.Count == 0)
        {
            return (false, null, null);
        }

        if (matches.Count > 1)
        {
            var signatures = string.Join(
                "; ",
                matches.Select(match => FormatMethodSignature(match.Candidate)));
            throw new InvalidOperationException(
                $"Multiple overloads matched special method '{Name}.{methodName}' with {arguments.Count} argument(s): {signatures}.");
        }

        var winner = matches[0].Candidate;
        var locals = matches[0].Locals;
        await _engine.ApplyPendingParameterDefaultsAsync(
            winner.Parameters,
            locals,
            matches[0].PendingDefaults,
            winner.SourceName,
            winner.SourceText,
            winner.CapturedScopes,
            $"{Name}.{winner.Name}",
            cancellationToken,
            ambient: CreateSelfBindings(instance));
        return (true, winner, locals);
    }

    private static IReadOnlyList<object?> UnwrapValues(IReadOnlyList<object?> values)
    {
        return values
            .Select(value => value is ToshClassSelfReference self ? self.Unwrap() : value)
            .ToArray();
    }

    private static object? FlattenCallResult(IReadOnlyList<object?> values)
    {
        var unwrapped = UnwrapValues(values);
        return unwrapped.Count switch
        {
            0 => null,
            1 => unwrapped[0],
            _ => unwrapped.ToArray(),
        };
    }

    private IReadOnlyList<ToshClassConstructorDefinition> GetConstructorDefinitions()
    {
        var constructors = new List<ToshClassConstructorDefinition>(_constructors);

        if (_primaryConstructorParameters.Count > 0)
        {
            constructors.Add(new ToshClassConstructorDefinition(
                _primaryConstructorParameters,
                new BlockSyntax(Array.Empty<Parsing.StatementSyntax>(), Span),
                SourceName,
                SourceText,
                Span,
                CapturedScopes));
        }

        if (constructors.Count == 0)
        {
            constructors.Add(new ToshClassConstructorDefinition(
                Array.Empty<FunctionParameterDefinition>(),
                new BlockSyntax(Array.Empty<Parsing.StatementSyntax>(), Span),
                SourceName,
                SourceText,
                Span,
                CapturedScopes));
        }

        return constructors;
    }

    private IReadOnlyList<ShellConstructorDescriptor> GetConstructorMetadata()
    {
        return GetConstructorDefinitions()
            .Select(constructor => new ShellConstructorDescriptor(
                constructor.Parameters.Count,
                FormatConstructorSignature(constructor.Parameters)))
            .ToArray();
    }

    private string FormatMethodSignature(ToshClassMethodDefinition method)
    {
        var modifier = method.IsStatic ? "static " : string.Empty;
        return $"{modifier}{GetAnnotationDisplayName(method.ReturnTypeName)} {method.Name}({FormatParameters(method.Parameters)})";
    }

    private string FormatConstructorSignature(IReadOnlyList<FunctionParameterDefinition> parameters)
    {
        return $"{Name}({FormatParameters(parameters)})";
    }

    private static string FormatParameters(IReadOnlyList<FunctionParameterDefinition> parameters)
    {
        return string.Join(
            ", ",
            parameters.Select(parameter =>
            {
                var suffix = parameter.IsOptional ? "?" : string.Empty;
                var rest = parameter.IsRest ? "..." : string.Empty;
                return parameter.TypeName is { Length: > 0 }
                    ? $"{parameter.Name}{suffix}{rest}: {parameter.TypeName}"
                    : $"{parameter.Name}{suffix}{rest}";
            }));
    }

    private static string GetAnnotationDisplayName(string? typeName)
    {
        return string.IsNullOrWhiteSpace(typeName) ? typeof(object).FullName ?? typeof(object).Name : typeName;
    }
}
