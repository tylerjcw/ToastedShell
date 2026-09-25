using System.Globalization;
using Tosh.Runtime;
using Tosh.Language.Parsing;

namespace Tosh.Language;

public sealed partial class ToshClassDefinition : IShellNamedType
{
    private readonly ToshEngine _engine;
    private readonly Dictionary<string, ToshClassPropertyDefinition> _propertiesByName;
    private readonly Dictionary<string, IReadOnlyList<ToshClassMethodDefinition>> _methodsByName;
    private readonly List<ToshClassConstructorDefinition> _constructors;
    private readonly IReadOnlyList<FunctionParameterDefinition> _primaryConstructorParameters;
    private readonly Dictionary<string, object?> _staticValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ToshClassPropertyDefinition> _properties;
    private readonly List<ToshClassMethodDefinition> _methods;

    /// <summary>
    /// Functions bound from a <c>bind native</c> block in the class body. Always
    /// static — an instance-bound P/Invoke is never what anyone wants — and
    /// <c>shy</c> unless the block was declared <c>proud</c>, so the raw ABI
    /// surface stays hidden behind typed members written over it.
    /// </summary>
    private readonly Dictionary<string, (IShellCommand Command, bool IsShy)> _nativeMembers =
        new(StringComparer.OrdinalIgnoreCase);

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
        IReadOnlyList<ToshTypeParameterConstraint>? typeParameterConstraints = null,
        DocComment? documentation = null)
    {
        _engine = engine;
        Documentation = documentation;
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

    public bool IsFluid { get; internal set; }

    /// <summary>
    /// Merges members from another partial class definition into this one.
    /// Properties, methods, and constructors from the other definition are added.
    /// </summary>
    /// <summary>
    /// Rejects two constructors the overload resolver could never tell apart — <c>TS-P1-34</c>'s
    /// sibling, <c>TS-P1-18</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A class declaring a primary constructor <c>C(x: int)</c> and an explicit <c>C(y: int)</c>
    /// registered both, so *every* instantiation failed with "Multiple constructor overloads
    /// matched class 'C' with 1 argument(s): C(y: int); C(x: int)" — a class reported as ambiguous
    /// with itself, at the point of use, with nothing naming the declaration at fault.
    /// </para>
    /// <para>
    /// The rule compares **type annotations positionally**, not arity. Same arity is emphatically
    /// legal and works today: `G(n: int)` beside `G(s: string)` resolves correctly, and so does a
    /// primary `H(x: int)` beside an explicit `H(s: string)`. A blanket arity rule would have
    /// broken both. Only an *identical* signature is rejected, which is exactly the case the
    /// resolver cannot decide — so this can turn no working program into an error, since any
    /// program it rejects could not be instantiated at that signature in the first place.
    /// </para>
    /// <para>
    /// An unannotated parameter is its own signature and collides only with another unannotated
    /// one in the same position. `C(y: int)` beside `C(x)` stays legal: they are distinguishable,
    /// the typed one being preferred for an <c>int</c> and the untyped one taking everything else.
    /// </para>
    /// </remarks>
    internal void ValidateConstructorSignatures()
    {
        var seen = new List<(IReadOnlyList<FunctionParameterDefinition> Parameters, bool IsPrimary)>();

        foreach (var constructor in _constructors)
        {
            seen.Add((constructor.Parameters, false));
        }

        if (_primaryConstructorParameters.Count > 0)
        {
            seen.Add((_primaryConstructorParameters, true));
        }

        for (var i = 0; i < seen.Count; i++)
        {
            for (var j = i + 1; j < seen.Count; j++)
            {
                if (!SignaturesCollide(seen[i].Parameters, seen[j].Parameters))
                {
                    continue;
                }

                var involvesPrimary = seen[i].IsPrimary || seen[j].IsPrimary;
                var signature = FormatConstructorSignature(seen[j].Parameters);

                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.duplicate_constructor",
                    Title: involvesPrimary
                        ? $"Class '{Name}' declares a constructor with the same signature as its primary constructor."
                        : $"Class '{Name}' declares two constructors with the same signature.",
                    SourceName: SourceName,
                    SourceText: SourceText,
                    Span: Span,
                    Label: $"both are {signature}",
                    Help: involvesPrimary
                        ? "give the explicit constructor a different signature, or drop the primary "
                          + "constructor's parameters and let the explicit one take them."
                        : "give one of them a different signature."));
            }
        }
    }

    /// <summary>
    /// Two parameter lists the resolver cannot distinguish: same length, and the same declared
    /// type in every position (an absent annotation matching only another absent annotation).
    /// </summary>
    private static bool SignaturesCollide(
        IReadOnlyList<FunctionParameterDefinition> left,
        IReadOnlyList<FunctionParameterDefinition> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index].TypeName, right[index].TypeName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

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

    public bool TryGetDeclaredProperty(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ToshClassPropertyDefinition? property)
    {
        for (var current = this; current is not null; current = current.BaseClass)
        {
            if (current._propertiesByName.TryGetValue(name, out property))
            {
                return true;
            }
        }

        property = null;
        return false;
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

    private async Task<object> CreateInstanceCoreAsync(
        IReadOnlyList<object?> arguments,
        IReadOnlyDictionary<string, Type?>? typeArgumentBindings,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? nominalTypeArguments = null)
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

        var instance = new ToshClassInstance(this, typeArgumentBindings, nominalTypeArguments);
        await ConstructOnInstanceAsync(instance, arguments, cancellationToken);
        instance.CompleteInitialization();

        // Validate that all vital (required) properties have been set to non-null values.
        // The whole chain is walked, not just this class: a `vital` property declared on a base
        // is still required of everything built from it, and checking only `Properties` — which
        // holds what this class itself declares — let `class D extends B { }` construct while
        // leaving B's vital property unset.
        for (var declaring = this; declaring is not null; declaring = declaring.BaseClass)
        {
            foreach (var property in declaring.Properties.Where(p => p.IsVital && !p.IsStatic && !p.IsComputed))
            {
                if (instance.TryGetStoredValue(property.Name, out var value) && value is null)
                {
                    throw new InvalidOperationException(
                        $"Vital property '{property.Name}' on class '{declaring.Name}' must be provided a value. " +
                        $"Set it in the constructor or provide an initializer.");
                }
            }
        }

        return instance;
    }


    /// <summary>
    /// Whether this class (or a base) declares a static method by that name.
    /// </summary>
    /// <remarks>
    /// Exposed for `TS-P2-94`, so <c>&amp;C.Static</c> can be recognised without a
    /// speculative invocation. Arity is deliberately not consulted: the reference
    /// stands for the whole overload set, and picking among them is the
    /// dispatcher's job at call time, not the reference's at capture time.
    /// </remarks>
    /// <summary>
    /// The class's own `##` documentation, when it was written (`TS-P2-101`).
    /// </summary>
    public DocComment? Documentation { get; }

    /// <inheritdoc />
    public string? ShellDocumentation => Documentation?.Description is { Length: > 0 } summary
        ? summary
        : null;

    /// <summary>
    /// Whether an instance of this class has anything reachable by <paramref name="name"/>.
    /// </summary>
    /// <remarks>
    /// A *lookup*, never an evaluation: `TryGetInstanceMember` runs property getters, so it
    /// cannot be used to ask a question. A class with a CLR base answers `true` for anything
    /// it does not declare itself, because the real check there needs the base object and
    /// this is asked without one — erring toward "it has it" keeps a name from being read as
    /// something else on a maybe.
    /// </remarks>
    internal bool HasInstanceMember(string name)
    {
        if (_methodsByName.TryGetValue(name, out var candidates) &&
            candidates.Any(method => !method.IsStatic))
        {
            return true;
        }

        if (_propertiesByName.ContainsKey(name) || ClrBaseType is not null)
        {
            return true;
        }

        return BaseClass is not null && BaseClass.HasInstanceMember(name);
    }

    /// <summary>
    /// Whether this class itself declares an instance method of this name — no base chain, no
    /// traits, no CLR fall-through.
    /// </summary>
    /// <remarks>
    /// `TOAST-0055`. <see cref="HasInstanceMember"/> cannot answer this: it reports true for
    /// any name at all once <c>ClrBaseType</c> is set, which is right for member dispatch and
    /// useless for asking whether an operator was overloaded. Walking the chain is the
    /// caller's job, because it also has traits to consult.
    /// </remarks>
    internal bool DeclaresInstanceMethodNamed(string methodName)
        => _methodsByName.TryGetValue(methodName, out var candidates) &&
           candidates.Any(method => !method.IsStatic);

    /// <summary>Whether <c>new T()</c> would have every parameter it needs.</summary>
    /// <remarks>
    /// `TOAST-0055`, for <c>where T: new()</c>. A class with no constructor at all takes no
    /// arguments; otherwise some constructor — primary or declared — must be satisfiable with
    /// none, which means every parameter is optional or a rest parameter.
    /// </remarks>
    public bool IsConstructibleWithoutArguments
    {
        get
        {
            if (_constructors.Count == 0 && _primaryConstructorParameters.Count == 0)
            {
                return true;
            }

            if (TakesNoRequiredArguments(_primaryConstructorParameters) &&
                _primaryConstructorParameters.Count > 0)
            {
                return true;
            }

            foreach (var constructor in _constructors)
            {
                if (TakesNoRequiredArguments(constructor.Parameters))
                {
                    return true;
                }
            }

            return false;
        }
    }

    private static bool TakesNoRequiredArguments(IReadOnlyList<FunctionParameterDefinition> parameters)
        => parameters.All(parameter => parameter.IsOptional || parameter.IsRest);

}
