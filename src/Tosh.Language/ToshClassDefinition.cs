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
        IReadOnlyList<string>? typeParameters = null)
    {
        _engine = engine;
        Name = name;
        TypeParameterNames = typeParameters ?? Array.Empty<string>();
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

    public IReadOnlyList<ToshClassPropertyDefinition> Properties { get; }

    public IReadOnlyList<ToshClassMethodDefinition> Methods { get; }

    public bool HasPrimaryConstructor => _primaryConstructorParameters.Count > 0;

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

    public bool ShellIsGenericType => false;

    public bool ShellIsArray => false;

    public bool ShellIsPublic => true;

    public object CreateInstance(IReadOnlyList<object?> arguments)
    {
        return CreateInstanceCore(arguments, typeArgumentBindings: null);
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

        return CreateInstanceCore(arguments, bindings);
    }

    private object CreateInstanceCore(
        IReadOnlyList<object?> arguments,
        IReadOnlyDictionary<string, Type?>? typeArgumentBindings)
    {
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

        var constructor = SelectConstructor(arguments, out var locals);
        var instance = new ToshClassInstance(this, typeArgumentBindings);
        ValidateConstructorTypeArguments(constructor!, locals, instance);
        instance.Initialize(locals, constructor);

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
        if (!_methodsByName.TryGetValue(methodName, out var candidates))
        {
            throw new InvalidOperationException($"Static method '{methodName}' was not found on class '{Name}'.");
        }

        var staticCandidates = candidates.Where(candidate => candidate.IsStatic && !candidate.IsShy).ToArray();

        if (staticCandidates.Length == 0)
        {
            throw new InvalidOperationException($"'{methodName}' is an instance method on class '{Name}' and cannot be called statically. Create an instance first: var obj = new {Name}(); $obj.{methodName}(...)");
        }

        var method = SelectMethod(staticCandidates, arguments, out var locals);
        var values = ExecuteMethodBlock(method, locals, instance: null);
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

            // Lazy property: evaluate initializer on first access
            if (property.IsLazy && !instance.IsLazyInitialized(property.Name))
            {
                instance.MarkLazyInitialized(property.Name);
                var lazyValue = GetInitialPropertyValue(instance, property, new Dictionary<string, object?>(StringComparer.Ordinal));
                instance.SetStoredValue(property.Name, lazyValue);
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

    internal void InvokeConstructorOnInstance(ToshClassInstance instance, IReadOnlyList<object?> arguments)
    {
        var constructor = SelectConstructor(arguments, out var ctorLocals);
        if (constructor is not null)
        {
            RunConstructor(instance, constructor, ctorLocals);
        }
    }

    internal InvocationResult InvokeInstanceMethod(ToshClassInstance instance, string methodName, IReadOnlyList<object?> arguments, bool includeHidden)
    {
        if (!_methodsByName.TryGetValue(methodName, out var candidates))
        {
            // Allow calling the constructor by class name (e.g. $super.BaseClass(args))
            if (string.Equals(methodName, Name, StringComparison.OrdinalIgnoreCase))
            {
                var constructor = SelectConstructor(arguments, out var ctorLocals);
                if (constructor is not null)
                {
                    RunConstructor(instance, constructor, ctorLocals);
                }
                return new InvocationResult(null, ReturnedVoid: true);
            }

            if (BaseClass is not null)
            {
                return BaseClass.InvokeInstanceMethod(instance, methodName, arguments, includeHidden);
            }

            if (ClrBaseType is not null && instance.ClrBaseObject is not null)
            {
                var result = _engine.Runtime.Invoker.InvokeInstance(instance.ClrBaseObject, methodName, arguments);
                return new InvocationResult(result, ReturnedVoid: false);
            }

            throw new InvalidOperationException($"Method '{methodName}' was not found on class '{Name}'.");
        }

        var method = SelectMethod(
            candidates.Where(candidate => !candidate.IsStatic && (includeHidden || (!candidate.IsShy && !candidate.IsGuarded && !candidate.IsLocal))).ToArray(),
            arguments,
            out var locals);

        // Emit deprecation warning for fading methods
        if (method.IsFading)
        {
            _engine.WriteWarning(
                code: "tosh.runtime.fading_member",
                title: $"Method '{method.Name}' on class '{Name}' is fading (deprecated).",
                help: "Use a non-fading replacement, or hush this code: hush tosh.runtime.fading_member",
                category: Tosh.Runtime.ToshDiagnosticCategory.Deprecation);
        }

        var values = ExecuteMethodBlock(method, locals, instance);
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

    internal bool HasSpecialInstanceMethod(string methodName, IReadOnlyList<object?> arguments)
    {
        return TrySelectSpecialInstanceMethod(methodName, arguments, out _, out _);
    }

    internal bool TryInvokeSpecialInstanceMethod(ToshClassInstance instance, string methodName, IReadOnlyList<object?> arguments, out object? value)
    {
        value = null;

        if (!TrySelectSpecialInstanceMethod(methodName, arguments, out var method, out var locals))
        {
            return false;
        }

        var values = ExecuteMethodBlock(method, locals, instance);
        value = FlattenCallResult(values);
        return true;
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

    internal void RunConstructor(ToshClassInstance instance, ToshClassConstructorDefinition constructor, IReadOnlyDictionary<string, object?> constructorLocals)
    {
        var locals = CreateLocals(instance, constructorLocals);
        _engine.ExecuteClassBlockSync(constructor.SourceName, constructor.SourceText, constructor.Body, locals, constructor.CapturedScopes, $"{Name}()");
    }

    internal IReadOnlyList<object?> EvaluateBaseConstructorArgs(IReadOnlyDictionary<string, object?> constructorLocals)
    {
        if (BaseConstructorArgs is null or { Count: 0 })
        {
            return Array.Empty<object?>();
        }

        var args = new List<object?>();
        foreach (var argPipeline in BaseConstructorArgs)
        {
            var value = _engine.EvaluateClassPipelineValueSync(SourceName, SourceText, argPipeline, constructorLocals, CapturedScopes);
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

    private void ExecutePropertySetter(ToshClassInstance instance, ToshClassPropertyDefinition property, object? value)
    {
        var locals = CreateLocals(instance, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["value"] = value,
        });
        _engine.ExecuteClassBlockSync(SourceName, SourceText, property.SetterBody!, locals, CapturedScopes, $"{Name}.{property.Name}.set");
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

    private IReadOnlyList<object?> ExecuteMethodBlock(ToshClassMethodDefinition method, IReadOnlyDictionary<string, object?> boundLocals, ToshClassInstance? instance)
    {
        // For generic instance methods, validate any parameters whose original
        // (un-erased) annotation references a class type-parameter and
        // substitute the instance's binding before running the body.
        if (instance is not null && !method.IsStatic)
        {
            var bindings = instance.GetBindingsFor(this);
            if (bindings is { Count: > 0 })
            {
                foreach (var parameter in method.Parameters)
                {
                    if (parameter.RawTypeName is null) continue;
                    if (!bindings.TryGetValue(parameter.RawTypeName, out var bound)) continue;
                    if (bound is null) continue; // unresolved type-parameter binding
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

        var locals = CreateLocals(instance, boundLocals);
        var values = _engine.ExecuteClassBlockSync(method.SourceName, method.SourceText, method.Body, locals, method.CapturedScopes, $"{Name}.{method.Name}");

        // Resolve the effective return-type annotation: prefer the un-erased
        // RawReturnTypeName when it names a bound class type-parameter,
        // otherwise fall through to the (possibly-erased) ReturnTypeName.
        // When the return type is bound from a type-parameter we apply a
        // strict no-coercion check; otherwise we go through the engine's
        // standard annotated-value conversion path.
        Type? strictReturnBinding = null;
        string? effectiveReturnType = method.ReturnTypeName;
        if (instance is not null && method.RawReturnTypeName is not null)
        {
            var bindings = instance.GetBindingsFor(this);
            if (bindings is not null && bindings.TryGetValue(method.RawReturnTypeName, out var bound))
            {
                if (bound is null)
                {
                    // Type parameter is recognised but unresolved — accept any value.
                    effectiveReturnType = null;
                }
                else
                {
                    strictReturnBinding = bound;
                    effectiveReturnType = bound.FullName ?? bound.Name;
                }
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

        return effectiveReturnType is null
            ? UnwrapValues(values)
            : UnwrapValues(values)
                .Select(value => _engine.ConvertAnnotatedValue(effectiveReturnType, value, method.Span, method.SourceName, method.SourceText, $"{Name}.{method.Name}"))
                .ToArray();
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

    private ToshClassConstructorDefinition? SelectConstructor(IReadOnlyList<object?> arguments, out Dictionary<string, object?> locals)
    {
        var constructors = GetConstructorDefinitions();
        var matches = _engine.SelectBestCallableMatches(constructors, static candidate => candidate.Parameters, arguments);

        if (matches.Count == 0)
        {
            if (constructors.Count == 1)
            {
                ThrowDetailedSingleConstructorMismatch(constructors[0], arguments);
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

        locals = matches[0].Locals;
        return matches[0].Candidate;
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

    private ToshClassMethodDefinition SelectMethod(IReadOnlyList<ToshClassMethodDefinition> candidates, IReadOnlyList<object?> arguments, out Dictionary<string, object?> locals)
    {
        var matches = _engine.SelectBestCallableMatches(candidates, static candidate => candidate.Parameters, arguments);

        if (matches.Count == 0)
        {
            var methodDisplayName = candidates.Count > 0 ? $"'{Name}.{candidates[0].Name}'" : $"'{Name}'";
            throw new InvalidOperationException($"No overload matched {methodDisplayName} with {arguments.Count} argument(s).");
        }

        if (matches.Count > 1)
        {
            var methodDisplayName = candidates.Count > 0 ? $"{Name}.{candidates[0].Name}" : Name;
            var signatures = string.Join(
                "; ",
                matches.Select(match => FormatMethodSignature(match.Candidate)));
            throw new InvalidOperationException(
                $"Multiple overloads matched method '{methodDisplayName}' with {arguments.Count} argument(s): {signatures}.");
        }

        locals = matches[0].Locals;
        return matches[0].Candidate;
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

    private bool TrySelectSpecialInstanceMethod(
        string methodName,
        IReadOnlyList<object?> arguments,
        out ToshClassMethodDefinition method,
        out Dictionary<string, object?> locals)
    {
        if (!_methodsByName.TryGetValue(methodName, out var candidates))
        {
            if (BaseClass is not null)
            {
                return BaseClass.TrySelectSpecialInstanceMethod(methodName, arguments, out method, out locals);
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
        return true;
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
