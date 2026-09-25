using System.Globalization;
using Tosh.Runtime;
using Tosh.Language.Parsing;

namespace Tosh.Language;

public sealed partial class ToshClassDefinition
{
    /// <summary>
    /// Instantiates a generic class definition by binding its type parameters to the provided
    /// CLR types. Fails if the argument count does not match the declaration, or if any bound
    /// type argument violates a constraint declared on that parameter.
    /// <paramref name="typeArgumentDisplay"/> carries the original source tokens for
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

        // `TOAST-0125`. The names as written, kept beside the resolved types. A ToastScript
        // type argument resolves to null on purpose — see `ResolveTypeArgument` — and without
        // the name the instance could neither say nor check what it was closed over.
        var nominal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < TypeParameterNames.Count; i++)
        {
            bindings[TypeParameterNames[i]] = resolvedTypeArguments[i];

            if (resolvedTypeArguments[i] is null && i < typeArgumentDisplay.Count)
            {
                nominal[TypeParameterNames[i]] = typeArgumentDisplay[i];
            }
        }

        ValidateTypeParameterConstraints(bindings, typeArgumentDisplay);

        return await CreateInstanceCoreAsync(
            arguments, bindings, cancellationToken, nominal.Count > 0 ? nominal : null);
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
                        // `TOAST-0055`. A ToastScript declaration has no CLR type of its own —
                        // user classes share a backing type — so the bound arrives null and
                        // every built-in check used to be skipped here, on the ground that
                        // precise enforcement happens at the next concrete instantiation. For a
                        // declared type that instantiation never comes, so `where T: Numeric`
                        // accepted any class at all. Ask the declaration instead; a null answer
                        // still means no opinion, and only then is it forwarded or unresolved.
                        var declared = ResolveDeclaredTypeArgument(argDisplay);
                        var verdict = declared is null
                            ? null
                            : ToshDeclaredTypeConstraints.Evaluate(constraintName, declared);

                        if (verdict is null)
                        {
                            continue;
                        }

                        satisfied = verdict.Value;
                        known = true;
                    }
                    else
                    {
                        satisfied = predicate(bound);
                        known = true;
                    }
                }
                else
                {
                    satisfied = TrySatisfyUserConstraint(constraintName, bound, argDisplay, out known);
                }

                // `TOAST-0055`. A name that resolves to nothing is a typo, and accepting it
                // does not leave the constraint unchecked — it deletes it, while the
                // declaration goes on reading as though it constrained something. Asked
                // before `satisfied`, which is defaulted true for a name nobody could check.
                if (!known && !_engine.IsRecognisedConstraintName(constraintName))
                {
                    throw new InvalidOperationException(
                        $"Generic class '{Name}' constrains '{clause.TypeParameter}' to "
                        + $"'{constraintName}', which is not a known constraint — "
                        + _engine.UnrecognisedConstraintHelp(constraintName));
                }

                if (satisfied) continue;
                if (!known) continue; // recognised, but not checkable from here

                // The CLR name is worth showing when there is one and worth omitting when
                // there is not: a ToastScript class has no CLR type of its own, and saying
                // `(CLR <unresolved>)` about it reads as a second failure.
                var detail = bound is null
                    ? $"'{argDisplay}'"
                    : $"'{argDisplay}' (CLR {bound.FullName ?? bound.Name})";

                throw new InvalidOperationException(
                    $"Generic class '{Name}' requires type parameter '{clause.TypeParameter}' to satisfy "
                    + $"'{constraintName}', but {detail} does not.");
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
            // `TOAST-0125`. A CLR type cannot implement a ToastScript interface, so a bound
            // argument here is a definite failure rather than something to be conservative
            // about. `where T: Drawable` accepted `int` before this, which is the shape of
            // constraint most likely to be written and the one least likely to be checked
            // anywhere else.
            if (bound is not null)
            {
                known = true;
                return false;
            }

            // Genuinely unrecognised: neither a CLR type nor a declaration this session
            // knows. Stay conservative — refusing what cannot be checked turns an
            // unenforced constraint into a wrong error.
            known = false;
            return true;
        }

        // `TOAST-0125`. A base-class constraint: `where T: Shape` is satisfied by `Shape`
        // itself and by anything extending it, and by nothing else. It used to be accepted
        // conservatively, which meant never checked at all.
        if (_engine.TryGetNamedType(constraintName, out var namedConstraint)
            && namedConstraint is ToshClassDefinition constraintClass)
        {
            var argLookup = StripGenericTypeArguments(argDisplay);

            if (_engine.TryGetNamedType(argLookup, out var argNamed) &&
                argNamed is ToshClassDefinition argumentClass)
            {
                known = true;
                return ClassExtends(argumentClass, constraintClass.Name);
            }

            if (bound is not null)
            {
                // A CLR type cannot extend a ToastScript class.
                known = true;
                return false;
            }

            known = false;
            return true;
        }

        // Constraint name resolves to some other shell-named type — accept conservatively.
        if (_engine.TryGetNamedType(constraintName, out _))
        {
            known = false;
            return true;
        }

        known = false;
        return false;
    }

    /// <summary>The declaration a type-argument name refers to, if it names one.</summary>
    /// <remarks>
    /// `TOAST-0055`. The display name is what the caller wrote — `Thing`, or `Box&lt;int&gt;`
    /// — because a declared type cannot be identified by its CLR backing, which every user
    /// class shares.
    /// </remarks>
    private object? ResolveDeclaredTypeArgument(string argDisplay)
        => _engine.TryGetNamedType(StripGenericTypeArguments(argDisplay), out var named) ? named : null;

    private static string StripGenericTypeArguments(string typeName)
    {
        var lt = typeName.IndexOf('<');
        return lt < 0 ? typeName.Trim() : typeName.Substring(0, lt).Trim();
    }

    /// <summary>
    /// True when this class, or anything it inherits from, fulfills an interface
    /// or uses a trait of the given name.
    ///
    /// Interfaces and traits are both "contracts a class satisfies" as far as an
    /// annotation is concerned — `func render(d: Drawable)` should accept any
    /// class that fulfills `Drawable`, and the same for a trait. Kept separate
    /// from <see cref="ClassImplementsInterface"/>, which answers only the
    /// interface half for `is`.
    /// </summary>
    internal bool SatisfiesContract(string contractName)
    {
        var current = this;

        while (current is not null)
        {
            foreach (var iface in current.ImplementedInterfaces)
            {
                if (string.Equals(iface.Name, contractName, StringComparison.OrdinalIgnoreCase)) return true;
            }

            foreach (var trait in current.UsedTraits)
            {
                if (string.Equals(trait.Name, contractName, StringComparison.OrdinalIgnoreCase)) return true;
            }

            current = current.BaseClass;
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="cls"/> is <paramref name="baseName"/> or extends it —
    /// <c>TOAST-0125</c>.
    /// </summary>
    /// <remarks>
    /// A class satisfies its own name, as it does in C#: `where T: Shape` takes a `Shape`.
    /// </remarks>
    private static bool ClassExtends(ToshClassDefinition cls, string baseName)
    {
        for (var current = cls; current is not null; current = current.BaseClass)
        {
            if (string.Equals(current.Name, baseName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
}
