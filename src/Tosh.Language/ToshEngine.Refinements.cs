using System.Text;
using Tosh.Runtime;
using Tosh.Language.Parsing;

namespace Tosh.Language;

/// <summary>
/// Refinement types and annotations: declaring a refinement, deciding whether a value
/// satisfies one, and converting a value to an annotated type.
///
/// Moved out of ToshEngine.cs by `TOAST-0005`. Every member moved **verbatim**.
///
/// The largest concern in the engine by member count, and the one with the most
/// overloads — `ConvertAnnotatedValue`, `ConvertAnnotatedValueAsync`,
/// `TryConvertAnnotatedValue`, `TryConvertAnnotatedValueAsync` and
/// `GetEffectiveAnnotatedTypeName` each have a sync/async or arity pair. They move
/// together; separating an overload from its sibling would be a change of shape rather
/// than a move, and `TS-P1-24` is the standing record of what happens when paired
/// implementations drift apart.
/// </summary>
public sealed partial class ToshEngine
{

    private RefinementTypeDefinition CreateRefinementTypeDefinition(
        string sourceName,
        string sourceText,
        TypeAliasStatementSyntax statement)
    {
        return new RefinementTypeDefinition(
            statement.Name,
            statement.TypeParameters,
            statement.BaseTypeName,
            CreateRefinementAnnotation(sourceName, sourceText, statement.Refinement),
            sourceName,
            sourceText,
            statement.Modifier,
            statement.Span,
            statement.DocComment?.Description?.Trim() is { Length: > 0 } desc ? desc : null);
    }

    private RefinementAnnotation? CreateRefinementAnnotation(
        string sourceName,
        string sourceText,
        ArgumentSyntax? predicate)
    {
        if (predicate is null)
        {
            return null;
        }

        if (predicate is RefinementClauseArgumentSyntax clause)
        {
            return new RefinementAnnotation(
                clause.Clauses.Select(static clause => clause switch
                {
                    RefinementWhereClauseSyntax whereClause => (RefinementClause)new RefinementWhereClause(whereClause.Predicate, whereClause.Span),
                    RefinementCoerceClauseSyntax coerceClause => new RefinementCoerceClause(coerceClause.Guard, coerceClause.Coercer, coerceClause.Span),
                    _ => throw new InvalidOperationException($"Unsupported refinement clause '{clause.GetType().Name}'."),
                }).ToArray(),
                sourceName,
                sourceText,
                clause.Span,
                CaptureVisibleScopes());
        }

        return new RefinementAnnotation(
            [new RefinementWhereClause(predicate, predicate.Span)],
            sourceName,
            sourceText,
            predicate.Span,
            CaptureVisibleScopes());
    }

    /// <summary>
    /// Lightweight recursive unifier for ctor / record-field
    /// inference. Handles three shapes:
    ///   • bare T            → bind T to value's runtime type
    ///   • Head&lt;args…&gt; on a generic CLR value → pointwise unify
    ///   • list/array/dict   → peek element / key&amp;value types
    /// First-binding-wins; later inconsistencies are tolerated and
    /// caught by the constraint validator (or the caller's bare-T
    /// fallback when inference is incomplete).
    /// </summary>
    private static void UnifyCtorAnnotationWithValue(
        HashSet<string> typeParameters,
        string annotation,
        object? value,
        Dictionary<string, Type> bindings)
    {
        annotation = annotation.Trim();
        if (annotation.Length == 0 || value is null) return;

        // Bare type-parameter reference.
        if (typeParameters.Contains(annotation))
        {
            if (!bindings.ContainsKey(annotation))
            {
                bindings[annotation] = value.GetType();
            }
            return;
        }

        var lt = annotation.IndexOf('<');
        var gt = annotation.LastIndexOf('>');
        if (lt <= 0 || gt != annotation.Length - 1) return;

        var head = annotation.Substring(0, lt).Trim();
        var inner = annotation.Substring(lt + 1, gt - lt - 1);
        var args = SplitTopLevelCommas(inner);
        if (args.Count == 0) return;

        switch (head.ToLowerInvariant())
        {
            case "list":
            case "array":
            case "ienumerable":
            case "icollection":
            case "ireadonlylist":
            case "ireadonlycollection":
                if (args.Count == 1 && TryGetElementType(value, out var elemType, out var elemSample))
                {
                    UnifyCtorAnnotationWithType(typeParameters, args[0].Trim(), elemType, elemSample, bindings);
                }
                return;

            case "dict":
            case "dictionary":
            case "map":
            case "idictionary":
            case "ireadonlydictionary":
                if (args.Count == 2 && TryGetDictionaryKVTypes(value, out var keyType, out var valType, out var keySample, out var valSample))
                {
                    UnifyCtorAnnotationWithType(typeParameters, args[0].Trim(), keyType, keySample, bindings);
                    UnifyCtorAnnotationWithType(typeParameters, args[1].Trim(), valType, valSample, bindings);
                }
                return;

            default:
                // Generic CLR type: read its bound type-args from the
                // runtime value and unify pointwise.
                var clrType = value.GetType();
                if (clrType.IsGenericType)
                {
                    var clrArgs = clrType.GetGenericArguments();
                    var pairs = Math.Min(clrArgs.Length, args.Count);
                    for (var i = 0; i < pairs; i++)
                    {
                        UnifyCtorAnnotationWithType(typeParameters, args[i].Trim(), clrArgs[i], sample: null, bindings);
                    }
                }
                return;
        }
    }

    private static void UnifyCtorAnnotationWithType(
        HashSet<string> typeParameters,
        string annotation,
        Type? clrType,
        object? sample,
        Dictionary<string, Type> bindings)
    {
        annotation = annotation.Trim();
        if (annotation.Length == 0) return;

        if (typeParameters.Contains(annotation))
        {
            if (bindings.ContainsKey(annotation)) return;
            if (clrType is not null && clrType != typeof(object))
            {
                bindings[annotation] = clrType;
            }
            else if (sample is not null)
            {
                bindings[annotation] = sample.GetType();
            }
            return;
        }

        // Recurse into nested annotations using the CLR type only;
        // we don't have a value to peek at this depth.
        var lt = annotation.IndexOf('<');
        var gt = annotation.LastIndexOf('>');
        if (lt <= 0 || gt != annotation.Length - 1) return;
        var inner = annotation.Substring(lt + 1, gt - lt - 1);
        var args = SplitTopLevelCommas(inner);
        if (args.Count == 0 || clrType is null || !clrType.IsGenericType) return;

        var clrArgs = clrType.GetGenericArguments();
        var pairs = Math.Min(clrArgs.Length, args.Count);
        for (var i = 0; i < pairs; i++)
        {
            UnifyCtorAnnotationWithType(typeParameters, args[i].Trim(), clrArgs[i], sample: null, bindings);
        }
    }

    private void DeclareRefinementType(
        RefinementTypeDefinition definition,
        DeclarationModifier modifier,
        string? sourceName = null,
        string? sourceText = null,
        TextSpan? span = null,
        bool allowTypeNameConflict = false)
    {
        EnsureReservedBindingName(definition.Name);
        if (!allowTypeNameConflict)
        {
            EnsureRefinementAliasNameDoesNotConflictWithType(definition.Name, sourceName ?? definition.SourceName, sourceText ?? definition.SourceText, span ?? definition.Span);
        }

        if (modifier == DeclarationModifier.Default &&
            _scopes.Count > 0 &&
            _scopes.Peek() is { IsModuleScope: true, ExportDeclarationsByDefault: true } moduleScope)
        {
            moduleScope.RefinementTypes[definition.Name] = definition;
            moduleScope.Exports!.RefinementTypes[definition.Name] = definition;
            return;
        }

        if (modifier == DeclarationModifier.Export && TryGetNearestModuleScope(out var exportScope))
        {
            exportScope.RefinementTypes[definition.Name] = definition;
            exportScope.Exports!.RefinementTypes[definition.Name] = definition;
            return;
        }

        if (modifier == DeclarationModifier.Shy)
        {
            if (_scopes.Count == 0)
            {
                throw new InvalidOperationException("Shy type aliases require a function, block, or module scope.");
            }

            _scopes.Peek().RefinementTypes[definition.Name] = definition;
            return;
        }

        if (modifier is DeclarationModifier.Global or DeclarationModifier.Export)
        {
            Runtime.Classes[definition.Name] = definition;
            return;
        }

        if (_scopes.Count > 0)
        {
            _scopes.Peek().RefinementTypes[definition.Name] = definition;
            return;
        }

        Runtime.Classes[definition.Name] = definition;
    }

    private void PreRegisterRefinementTypeAliases(
        string sourceName,
        string sourceText,
        IReadOnlyList<StatementSyntax> statements)
    {
        foreach (var statement in statements.OfType<TypeAliasStatementSyntax>())
        {
            DeclareRefinementType(
                CreateRefinementTypeDefinition(sourceName, sourceText, statement),
                statement.Modifier,
                sourceName,
                sourceText,
                statement.Span);
        }
    }

    private void EnsureTypeNameDoesNotConflictWithRefinementAlias(
        string name,
        string? sourceName,
        string? sourceText,
        TextSpan? span,
        string declaredKind)
    {
        if (!TryGetRefinementType(name, out _))
        {
            return;
        }

        ThrowTypeNameConflict(
            sourceName,
            sourceText,
            span,
            code: "tosh.runtime.type_name_conflict",
            title: $"{declaredKind} '{name}' conflicts with an existing refinement alias.",
            label: $"'{name}' is already bound as a refinement alias",
            help: "choose a different name so types and refinement aliases stay distinct.");
    }

    private void EnsureRefinementAliasNameDoesNotConflictWithType(
        string name,
        string? sourceName,
        string? sourceText,
        TextSpan? span)
    {
        // Only block conflicts with user-declared named types
        // (classes, records, structs, enums, interfaces, traits,
        // unions, modules). The wider CLR-resolver fallback used to
        // be consulted here, but that scans every loaded assembly
        // for a type with a matching name and produces spurious
        // collisions for ordinary aliases like `Pair` (which clashes
        // with assorted CLR types such as `System.Web.UI.Pair`).
        // Authors get to pick their own alias names; the CLR
        // resolver only kicks in at use sites where an unqualified
        // type name needs disambiguating.
        if (!TryGetNamedType(name, out _))
        {
            return;
        }

        ThrowTypeNameConflict(
            sourceName,
            sourceText,
            span,
            code: "tosh.runtime.type_name_conflict",
            title: $"Refinement alias '{name}' conflicts with an existing type name.",
            label: $"'{name}' is already bound as a type",
            help: "choose a different alias name so refinements do not shadow real types.");
    }

    private bool TryGetRefinementType(string name, out RefinementTypeDefinition definition)
    {
        // Same reasoning as the class lookup in TryGetNamedType: a refinement
        // type named inside a class body belongs to the module that body lives
        // in, and by the time the annotation is checked that module's scope has
        // left the stack (`TS-P2-98`).
        if (AnnotationResolutionExports is { } declaringExports &&
            declaringExports.RefinementTypes.TryGetValue(name, out var declaredRefinement))
        {
            definition = declaredRefinement;
            return true;
        }

        foreach (var scope in _scopes)
        {
            if (scope.RefinementTypes.TryGetValue(name, out var scopedDefinition))
            {
                definition = scopedDefinition;
                return true;
            }
        }

        if (Runtime.Classes.TryGetValue(name, out var rawValue) &&
            rawValue is RefinementTypeDefinition runtimeDefinition)
        {
            definition = runtimeDefinition;
            return true;
        }

        if (TryResolveQualifiedModuleMember(name, out var qualified) &&
            qualified is RefinementTypeDefinition qualifiedRefinement)
        {
            definition = qualifiedRefinement;
            return true;
        }

        definition = null!;
        return false;
    }

    /// <summary>
    /// The diagnostic behind a failed annotated conversion, when the conversion produced one.
    /// </summary>
    /// <remarks>
    /// A refinement that rejects a value reports itself through an
    /// <c>AnnotationRefinementError</c> carried in the converted slot rather than by throwing, so
    /// the caller has to unwrap it. That unwrapping was written out once per surface, and a
    /// surface that forgot it would silently report "conversion failed" with no reason
    /// (<c>TS-P1-24</c>).
    /// </remarks>
    private static ToshDiagnosticException? DescribeAnnotationFailure(object? converted) =>
        converted is AnnotationRefinementError refinementError ? refinementError.Exception : null;

    /// <summary>
    /// A collection annotation whose single type argument names a tōast-declared type.
    /// </summary>
    /// <remarks>
    /// Returns false for anything else — a non-generic name, more than one argument, a
    /// non-collection head, an element type the CLR can represent (`list&lt;int&gt;` keeps
    /// its existing conversion), or a value that is not a sequence. Every element must
    /// satisfy the element type; one that does not means the annotation does not hold, and
    /// saying so is the point.
    /// </remarks>
    private bool TryConvertUserElementCollection(string typeName, object? value, out object? converted)
    {
        converted = null;

        if (value is null || value is string) { return false; }

        var lt = typeName.IndexOf('<');
        if (lt <= 0 || !typeName.EndsWith(">", StringComparison.Ordinal)) { return false; }

        var head = typeName[..lt].Trim().ToLowerInvariant();
        if (head is not ("list" or "array" or "seq" or "ienumerable" or "icollection"
            or "ireadonlylist" or "ireadonlycollection"))
        {
            return false;
        }

        var element = typeName[(lt + 1)..^1].Trim();
        if (element.Length == 0 || element.Contains(',')) { return false; }

        // Only when the element type is one this program declared. A CLR element type
        // already has a working conversion and must keep it.
        if (!TryGetNamedType(element, out _)) { return false; }

        if (value is not System.Collections.IEnumerable sequence) { return false; }

        foreach (var item in sequence)
        {
            if (item is null) { continue; }
            if (!OperatorEvaluator.IsInstanceOfShellType(item, element)) { return false; }
        }

        converted = value;
        return true;
    }

    internal bool TryConvertAnnotatedValue(string typeName, object? value, out object? converted)
        => TryConvertAnnotatedValue(typeName, value, out converted, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private string GetEffectiveAnnotatedTypeName(string typeName)
        => GetEffectiveAnnotatedTypeName(typeName, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private string GetEffectiveAnnotatedTypeName(string typeName, HashSet<string> activeRefinements)
    {
        var allowsNull = typeName.EndsWith("?", StringComparison.Ordinal);
        var normalizedTypeName = allowsNull ? typeName[..^1] : typeName;

        if (!TryResolveRefinementTypeForAnnotation(normalizedTypeName, out var refinementType) ||
            !activeRefinements.Add(refinementType.Name))
        {
            return typeName;
        }

        var effectiveBase = GetEffectiveAnnotatedTypeName(refinementType.BaseTypeName, activeRefinements);
        activeRefinements.Remove(refinementType.Name);
        return allowsNull && !effectiveBase.EndsWith("?", StringComparison.Ordinal)
            ? effectiveBase + "?"
            : effectiveBase;
    }

    private bool TryConvertAnnotatedValue(
        string typeName,
        object? value,
        out object? converted,
        HashSet<string> activeRefinements)
    {
        var allowsNull = typeName.EndsWith("?", StringComparison.Ordinal);
        var normalizedTypeName = allowsNull ? typeName[..^1] : typeName;

        if (value is ToshClassSelfReference selfReference)
        {
            value = selfReference.Unwrap();
        }

        if (value is null)
        {
            converted = null;
            return allowsNull;
        }

        if (TryResolveRefinementTypeForAnnotation(normalizedTypeName, out var refinementType))
        {
            if (!activeRefinements.Add(refinementType.Name))
            {
                converted = null;
                return false;
            }

            if (!TryConvertAnnotatedValue(refinementType.BaseTypeName, value, out var baseConverted, activeRefinements))
            {
                activeRefinements.Remove(refinementType.Name);
                converted = baseConverted;
                return false;
            }

            if (!TryApplyRefinementWithOptionalCoercion(refinementType.Refinement, baseConverted, out var refinedValue, out var failure))
            {
                activeRefinements.Remove(refinementType.Name);
                converted = failure is not null
                    ? new AnnotationRefinementError(failure)
                    : new AnnotationRefinementFailure(baseConverted, refinementType.Refinement);
                return false;
            }

            activeRefinements.Remove(refinementType.Name);
            converted = refinedValue;
            return true;
        }

        if (value is IShellTypedObject directTyped &&
            (string.Equals(directTyped.ShellTypeDescriptor.ShellTypeName, normalizedTypeName, StringComparison.Ordinal) ||
             string.Equals(directTyped.ShellTypeDescriptor.ShellFullName, normalizedTypeName, StringComparison.Ordinal)))
        {
            converted = value;
            return true;
        }

        if (TryGetNamedType(normalizedTypeName, out var shellType))
        {
            var shellDescriptor = (IShellTypeDescriptor)shellType;

            if (value is IShellTypedObject typed &&
                (string.Equals(typed.ShellTypeDescriptor.ShellTypeName, shellDescriptor.ShellTypeName, StringComparison.Ordinal) ||
                 string.Equals(typed.ShellTypeDescriptor.ShellFullName, shellDescriptor.ShellFullName, StringComparison.Ordinal)))
            {
                converted = value;
                return true;
            }

            // An interface or a trait is a contract, not a shape to convert to:
            // a value that fulfills it *is* already the annotated type. Without
            // this, `func render(d: Drawable)` rejected every class that
            // fulfilled `Drawable`, so a polymorphic signature could not be
            // annotated at all and had to fall back to duck typing
            // (`TS-P2-99`).
            if ((shellType is ToshInterfaceDefinition || shellType is ToshTraitDefinition) &&
                value is ToshClassInstance contractInstance &&
                contractInstance.Definition.SatisfiesContract(shellDescriptor.ShellTypeName))
            {
                converted = value;
                return true;
            }

            if (shellType is ToshEnumDefinition enumDefinition &&
                enumDefinition.TryConvertValue(value, out var enumValue))
            {
                converted = enumValue;
                return true;
            }

            if (shellType is ToshClassDefinition classDefinition &&
                value is ToshClassInstance classInstance)
            {
                for (var current = classInstance.Definition; current is not null; current = current.BaseClass)
                {
                    if (ReferenceEquals(current, classDefinition) ||
                        string.Equals(current.Name, classDefinition.Name, StringComparison.Ordinal))
                    {
                        converted = value;
                        return true;
                    }
                }
            }
        }

        // User-defined generic class annotation, e.g. 'Box<int>'. We accept a
        // ToshClassInstance whose definition (or any ancestor in the
        // inheritance chain) names the same generic class. Match before
        // falling through to ResolveTypeName, because angle-bracket-and-
        // comma syntax can throw "given assembly name was invalid" inside
        // the CLR type loader (Type.GetType treats commas as
        // type/assembly separators).
        if (TryGetGenericClassAnnotation(normalizedTypeName, out var genericClass, out _) &&
            value is ToshClassInstance genericInstance)
        {
            var current = genericInstance.Definition;
            while (current is not null)
            {
                if (ReferenceEquals(current, genericClass) ||
                    string.Equals(current.Name, genericClass.Name, StringComparison.Ordinal))
                {
                    converted = value;
                    return true;
                }
                current = current.BaseClass;
            }

            // Bare-name didn't match any ancestor; treat as a hard mismatch
            // rather than falling through to the CLR type-loader (which
            // would choke on the angle-bracketed name anyway).
            converted = null;
            return false;
        }

        // `TOAST-0038`. A collection whose element type is declared in tōast — `list<Token>`,
        // `array<Node>`.
        //
        // These could not be written at all. `list<int>` works because `List<int>` is a real
        // CLR type to convert to; a tōast class is a `ToshClassInstance`, so there is no
        // `List<Token>` for the conversion to target and every such annotation failed with
        // "could not be converted". A lexer returning `list<Token>` and a parser returning
        // `list<Node>` are the ordinary shapes of compiler-shaped code, which is how the
        // readiness probe found this.
        //
        // The elements are checked rather than converted, and the collection is handed back
        // as it is: `is` already answers "is this value that type" for a declared class, so
        // the annotation means what a reader would expect and no new rule is introduced.
        if (TryConvertUserElementCollection(normalizedTypeName, value, out var elementChecked))
        {
            converted = elementChecked;
            return true;
        }

        var resolvedType = ResolveTypeName(normalizedTypeName);

        if (resolvedType is not null &&
            TypeConversion.TryConvert(value, resolvedType, out converted))
        {
            return true;
        }

        // `TS-P1-47`, absorbed into `TOAST-0030`. After conversion, not before.
        //
        // An annotation and `is` have to agree about what a type name means, and
        // `OperatorEvaluator` is where that meaning lives — including, since cause D, the
        // walk up a CLR base chain. The compiled backend needs that here because nothing
        // above can see its classes: an emitted class is a real CLR type rather than a
        // registered shell definition, so `TryGetNamedType` finds nothing and every branch
        // that walks a `ToshClassInstance` is skipped. `var b: DiffBase = new DiffLeaf(4)`
        // then reached `TypeConversion.TryConvert`, which reported that a `DiffLeaf` could
        // not be converted to the base it already derives from.
        //
        // Trying it *first* was the obvious arrangement and it was wrong: an annotation can
        // legitimately retype a value it already matches, so `var a: array = [1, 2]` bound
        // as `array<int>` instead of `array`. Conversion means "make it this"; this check
        // only answers "it already is this", so it belongs where conversion has declined.
        if (value is not null && OperatorEvaluator.IsInstanceOfShellType(value, normalizedTypeName))
        {
            converted = value;
            return true;
        }

        if (resolvedType is not null)
        {
            converted = null;
            return false;
        }

        // Trait-style constraint names (Numeric, Add, Comparable, …) used as
        // a parameter type annotation: accept any value whose CLR type
        // satisfies the constraint predicate. Lets users write
        // `func +(other: Numeric)` to overload against arbitrary numeric
        // operands without having to enumerate every primitive type.
        if (ToshTypeParameterConstraintRegistry.TryGet(normalizedTypeName, out var constraintPredicate))
        {
            var clrType = value?.GetType();
            if (clrType is not null && constraintPredicate(clrType))
            {
                converted = value;
                return true;
            }

            converted = null;
            return false;
        }

        converted = null;
        return false;
    }

    /// <summary>
    /// Public bridge for compiled-IL refinement enforcement: converts
    /// (and validates) <paramref name="value"/> against the named
    /// annotated type, throwing a diagnostic on failure. Used by
    /// <c>Tosh.Compiler.Runtime.ToshHost.CheckType</c>.
    /// </summary>
    public object? ConvertValueToAnnotatedType(
        string typeName,
        object? value,
        int spanStart,
        int spanLength,
        string sourceName,
        string sourceText,
        string owner)
        => ConvertAnnotatedValue(typeName, value, new TextSpan(spanStart, spanLength), sourceName, sourceText, owner);

    internal object? ConvertAnnotatedValue(
        string? typeName,
        RefinementAnnotation? refinement,
        object? value,
        TextSpan span,
        string sourceName,
        string sourceText,
        string owner)
    {
        object? converted = value;

        if (typeName is not null)
        {
            if (!TryConvertAnnotatedValue(typeName, value, out converted))
            {
                if (converted is AnnotationRefinementFailure refinementFailure)
                {
                    return EnsureRefinementSatisfied(refinementFailure.Refinement, refinementFailure.Value, span, sourceName, sourceText, owner);
                }

                if (converted is AnnotationRefinementError refinementError)
                {
                    throw refinementError.Exception;
                }

                ThrowIfUnknownAnnotatedType(typeName, span, sourceName, sourceText, owner);
                throw AnnotationConversionFailure(typeName, value, span, sourceName, sourceText, owner);
            }

            if (value is null)
            {
                ThrowIfUnknownAnnotatedType(typeName, span, sourceName, sourceText, owner);
            }
        }

        return EnsureRefinementSatisfied(refinement, converted, span, sourceName, sourceText, owner);
    }

    /// <remarks>
    /// The unknown-type check runs only when the conversion *fails*. It produces the better
    /// diagnostic — "unknown type annotation 'itn'" rather than "cannot convert 3 to 'itn'" —
    /// but it answers its question by resolving the type name, which is exactly what the
    /// conversion just did. On the success path that was a second full resolution of a name
    /// already known to resolve, per assignment (`TS-P2-119`).
    ///
    /// A `null` accepted by a nullable annotation is the one case that never touches the type
    /// name, so it is checked explicitly — otherwise `var x: Nonexistent? = null` would bind
    /// happily against a type that does not exist.
    /// </remarks>
    internal object? ConvertAnnotatedValue(
        string typeName,
        object? value,
        TextSpan span,
        string sourceName,
        string sourceText,
        string owner)
    {
        if (TryConvertAnnotatedValue(typeName, value, out var converted))
        {
            if (value is null)
            {
                ThrowIfUnknownAnnotatedType(typeName, span, sourceName, sourceText, owner);
            }

            return converted;
        }

        if (converted is AnnotationRefinementFailure refinementFailure)
        {
            return EnsureRefinementSatisfied(refinementFailure.Refinement, refinementFailure.Value, span, sourceName, sourceText, owner);
        }

        if (converted is AnnotationRefinementError refinementError)
        {
            throw refinementError.Exception;
        }

        ThrowIfUnknownAnnotatedType(typeName, span, sourceName, sourceText, owner);
        throw AnnotationConversionFailure(typeName, value, span, sourceName, sourceText, owner);
    }

    /// <summary>
    /// The diagnostic for a value an annotation will not accept.
    /// </summary>
    /// <remarks>
    /// One helper because the message previously existed twice, word for word, on
    /// the two paths that raise it — the `TS-P1-24` shape, where whoever improves
    /// one copy improves one copy.
    ///
    /// It distinguishes the two refusals, because they call for different fixes
    /// (`TS-P2-111`). A fractional value refused by an integral annotation is a
    /// deliberate no-silent-data-loss decision and is answered by rounding;
    /// "the value does not match 'int'" reads as a type error and names no remedy,
    /// though the value is a perfectly good number.
    /// </remarks>
    private ToshDiagnosticException AnnotationConversionFailure(
        string typeName,
        object? value,
        TextSpan span,
        string sourceName,
        string sourceText,
        string owner)
    {
        if (TryResolveTypeName(typeName) is { } annotationType &&
            TypeConversion.WouldTruncate(value, annotationType))
        {
            return ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.annotation_conversion_failed",
                Title: $"'{owner}' produced {value}, which cannot become '{typeName}' without discarding its fractional part.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: span,
                Label: "round first with Math.Round, Math.Floor, Math.Ceiling or Math.Truncate"));
        }

        return ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.runtime.annotation_conversion_failed",
            Title: $"'{owner}' produced a value that could not be converted to '{typeName}'.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: span,
            Label: $"the value does not match '{typeName}'"));
    }

    internal async ValueTask<object?> ConvertAnnotatedValueAsync(
        string? typeName,
        RefinementAnnotation? refinement,
        object? value,
        TextSpan span,
        string sourceName,
        string sourceText,
        string owner,
        CancellationToken cancellationToken)
    {
        object? converted = value;

        if (typeName is not null)
        {
            ThrowIfUnknownAnnotatedType(typeName, span, sourceName, sourceText, owner);

            var conversion = await TryConvertAnnotatedValueAsync(
                typeName,
                value,
                cancellationToken);
            converted = conversion.Converted;

            if (!conversion.Success)
            {
                if (converted is AnnotationRefinementFailure refinementFailure)
                {
                    return await EnsureRefinementSatisfiedAsync(
                        refinementFailure.Refinement,
                        refinementFailure.Value,
                        span,
                        sourceName,
                        sourceText,
                        owner,
                        cancellationToken);
                }

                if (converted is AnnotationRefinementError refinementError)
                {
                    throw refinementError.Exception;
                }

                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.annotation_conversion_failed",
                    Title: $"'{owner}' produced a value that could not be converted to '{typeName}'.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: span,
                    Label: $"the value does not match '{typeName}'"));
            }
        }

        return await EnsureRefinementSatisfiedAsync(
            refinement,
            converted,
            span,
            sourceName,
            sourceText,
            owner,
            cancellationToken);
    }

    internal async ValueTask<object?> ConvertAnnotatedValueAsync(
        string typeName,
        object? value,
        TextSpan span,
        string sourceName,
        string sourceText,
        string owner,
        CancellationToken cancellationToken)
    {
        ThrowIfUnknownAnnotatedType(typeName, span, sourceName, sourceText, owner);

        var conversion = await TryConvertAnnotatedValueAsync(
            typeName,
            value,
            cancellationToken);
        if (conversion.Success)
        {
            return conversion.Converted;
        }

        if (conversion.Converted is AnnotationRefinementFailure refinementFailure)
        {
            return await EnsureRefinementSatisfiedAsync(
                refinementFailure.Refinement,
                refinementFailure.Value,
                span,
                sourceName,
                sourceText,
                owner,
                cancellationToken);
        }

        if (conversion.Converted is AnnotationRefinementError refinementError)
        {
            throw refinementError.Exception;
        }

        throw ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.runtime.annotation_conversion_failed",
            Title: $"'{owner}' produced a value that could not be converted to '{typeName}'.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: span,
            Label: $"the value does not match '{typeName}'"));
    }

    /// <summary>
    /// Internal rather than private so the annotated-conversion drift
    /// guard can run one corpus through both this and the synchronous
    /// <see cref="TryConvertAnnotatedValue(string, object?, out object?)"/>
    /// and assert they agree (TS-P1-24).
    /// </summary>
    internal ValueTask<(bool Success, object? Converted)> TryConvertAnnotatedValueAsync(
        string typeName,
        object? value,
        CancellationToken cancellationToken) =>
        TryConvertAnnotatedValueAsync(
            typeName,
            value,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            cancellationToken);

    private async ValueTask<(bool Success, object? Converted)> TryConvertAnnotatedValueAsync(
        string typeName,
        object? value,
        HashSet<string> activeRefinements,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var allowsNull = typeName.EndsWith("?", StringComparison.Ordinal);
        var normalizedTypeName = allowsNull ? typeName[..^1] : typeName;

        if (value is ToshClassSelfReference selfReference)
        {
            value = selfReference.Unwrap();
        }

        if (value is null)
        {
            return (allowsNull, null);
        }

        if (!TryResolveRefinementTypeForAnnotation(normalizedTypeName, out var refinementType))
        {
            return TryConvertAnnotatedValue(
                    typeName,
                    value,
                    out var converted,
                    activeRefinements)
                ? (true, converted)
                : (false, converted);
        }

        if (!activeRefinements.Add(refinementType.Name))
        {
            return (false, null);
        }

        try
        {
            var baseConversion = await TryConvertAnnotatedValueAsync(
                refinementType.BaseTypeName,
                value,
                activeRefinements,
                cancellationToken);
            if (!baseConversion.Success)
            {
                return baseConversion;
            }

            var refinement = await TryApplyRefinementWithOptionalCoercionAsync(
                refinementType.Refinement,
                baseConversion.Converted,
                cancellationToken);
            if (!refinement.Success)
            {
                return (
                    false,
                    refinement.Failure is not null
                        ? new AnnotationRefinementError(refinement.Failure)
                        : new AnnotationRefinementFailure(
                            baseConversion.Converted,
                            refinementType.Refinement));
            }

            return (true, refinement.RefinedValue);
        }
        finally
        {
            activeRefinements.Remove(refinementType.Name);
        }
    }

    private async ValueTask<object?> EnsureRefinementSatisfiedAsync(
        RefinementAnnotation? refinement,
        object? value,
        TextSpan span,
        string sourceName,
        string sourceText,
        string owner,
        CancellationToken cancellationToken)
    {
        if (refinement is null)
        {
            return value;
        }

        var result = await TryApplyRefinementWithOptionalCoercionAsync(
            refinement,
            value,
            cancellationToken);
        if (result.Failure is not null)
        {
            throw result.Failure;
        }

        if (result.Success)
        {
            return result.RefinedValue;
        }

        throw CreateRefinementFailedDiagnostic(refinement, span, sourceName, sourceText, owner);
    }

    private void ThrowIfUnknownAnnotatedType(
        string typeName,
        TextSpan span,
        string sourceName,
        string sourceText,
        string owner)
    {
        if (IsKnownAnnotatedType(typeName, new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
        {
            return;
        }

        var suggestion = ResolveNearestAnnotatedTypeSuggestion(typeName);
        var help = suggestion is null
            ? "define the type first, or use a known CLR/shell type name."
            : $"did you mean '{suggestion}'?";

        throw ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.runtime.annotation_unknown_type",
            Title: $"'{owner}' uses unknown type annotation '{typeName}'.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: span,
            Label: $"unknown type '{typeName}'",
            Help: help));
    }

    private bool IsKnownAnnotatedType(string typeName, HashSet<string> activeRefinements)
    {
        var allowsNull = typeName.EndsWith("?", StringComparison.Ordinal);
        var normalizedTypeName = allowsNull ? typeName[..^1] : typeName;

        if (TryResolveRefinementTypeForAnnotation(normalizedTypeName, out var refinementType))
        {
            if (!activeRefinements.Add(refinementType.Name))
            {
                return false;
            }

            var known = IsKnownAnnotatedType(refinementType.BaseTypeName, activeRefinements);
            activeRefinements.Remove(refinementType.Name);
            return known;
        }

        if (TryGetNamedType(normalizedTypeName, out _))
        {
            return true;
        }

        // User-defined generic class instantiation: 'Foo<int, string>'. Accept
        // when the bare name resolves to a ToshClassDefinition whose
        // type-parameter arity matches the supplied argument count, and
        // every supplied argument is itself a known annotated type. Check
        // this before ResolveTypeName because the CLR type loader can
        // throw on angle-bracketed names containing commas.
        if (TryGetGenericClassAnnotation(normalizedTypeName, out _, out var genericArgs))
        {
            foreach (var arg in genericArgs)
            {
                if (!IsKnownAnnotatedType(arg, activeRefinements))
                {
                    return false;
                }
            }
            return true;
        }

        return ResolveTypeName(normalizedTypeName) is not null;
    }

    private string? ResolveNearestAnnotatedTypeSuggestion(string typeName)
    {
        var normalized = typeName.EndsWith("?", StringComparison.Ordinal) ? typeName[..^1] : typeName;
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var scope in _scopes)
        {
            foreach (var name in scope.Classes.Keys)
            {
                candidates.Add(name);
            }

            foreach (var name in scope.RefinementTypes.Keys)
            {
                candidates.Add(name);
            }
        }

        foreach (var (name, _) in Runtime.Classes)
        {
            candidates.Add(name);
        }

        var bestMatch = (Name: (string?)null, Distance: int.MaxValue);
        foreach (var candidate in candidates)
        {
            var distance = LevenshteinDistance(normalized, candidate);
            if (distance < bestMatch.Distance)
            {
                bestMatch = (candidate, distance);
            }
        }

        return bestMatch.Name is not null &&
               bestMatch.Distance <= Math.Max(2, Math.Max(normalized.Length, bestMatch.Name.Length) * 2 / 5)
            ? bestMatch.Name
            : null;
    }

    /// <summary>
    /// Synchronous adapter over
    /// <see cref="TryApplyRefinementWithOptionalCoercionAsync"/> for the conversion
    /// callers that are not asynchronous.  The guard, predicate, and fallback-coercion
    /// sequence exists only in the asynchronous implementation (<c>TS-P1-24</c>);
    /// this method previously carried a second copy of it, and the whole synchronous
    /// sub-chain it drove — <c>TryApplyGuardedRefinementCoercion</c>,
    /// <c>TryEvaluateRefinementPredicate</c>, and the three leaf evaluators — has
    /// been removed with it.
    /// </summary>
    private bool TryApplyRefinementWithOptionalCoercion(
        RefinementAnnotation? refinement,
        object? value,
        out object? refinedValue,
        out ToshDiagnosticException? failure)
    {
        var (success, refined, applyFailure) = TryApplyRefinementWithOptionalCoercionAsync(
                refinement,
                value,
                CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();

        refinedValue = refined;
        failure = applyFailure;
        return success;
    }

    private async ValueTask<object?> EvaluateRefinementCoercerAsync(
        RefinementAnnotation refinement,
        RefinementCoerceClause clause,
        object? value,
        CancellationToken cancellationToken)
    {
        using var captured = PushCapturedScopes(refinement.CapturedScopes);
        using var currentValueScope = PushScope(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["_"] = value,
        });

        return await EvaluateArgumentAsync(
            refinement.SourceName,
            refinement.SourceText,
            clause.Coercer,
            cancellationToken);
    }

    private async ValueTask<bool> EvaluateRefinementPredicateAsync(
        RefinementAnnotation refinement,
        object? value,
        CancellationToken cancellationToken)
    {
        foreach (var clause in refinement.Clauses.OfType<RefinementWhereClause>())
        {
            if (!await EvaluateRefinementBooleanExpressionAsync(
                    refinement,
                    clause.Predicate,
                    value,
                    clause.Span,
                    "Refinement predicates",
                    cancellationToken))
            {
                return false;
            }
        }

        return true;
    }

    private async ValueTask<bool> EvaluateRefinementBooleanExpressionAsync(
        RefinementAnnotation refinement,
        ArgumentSyntax expression,
        object? value,
        TextSpan span,
        string title,
        CancellationToken cancellationToken,
        bool useTruthiness = false)
    {
        using var captured = PushCapturedScopes(refinement.CapturedScopes);
        using var currentValueScope = PushScope(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["_"] = value,
        });

        var predicateValue = await EvaluateArgumentAsync(
            refinement.SourceName,
            refinement.SourceText,
            expression,
            cancellationToken);

        if (useTruthiness)
        {
            return ToshTruthiness.IsTruthy(predicateValue);
        }

        if (predicateValue is not bool boolean)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.refinement_requires_boolean",
                Title: $"{title} must evaluate to boolean values.",
                SourceName: refinement.SourceName,
                SourceText: refinement.SourceText,
                Span: span,
                Label: "this refinement did not evaluate to true or false"));
        }

        return boolean;
    }

    /// <summary>
    /// Synchronous adapter over <see cref="EnsureRefinementSatisfiedAsync"/>.
    /// </summary>
    /// <remarks>
    /// This method used to inline its own copy of the guard/predicate/coerce
    /// sequence, and the copy had drifted: a non-diagnostic exception raised by the
    /// predicate <em>after</em> fallback coercion was reported against the coercer's
    /// span here and against the predicate's span on the asynchronous path. The
    /// asynchronous behaviour is canonical, so converging on it moved the
    /// synchronous span to the predicate (<c>TS-P1-24</c>).
    /// </remarks>
    private object? EnsureRefinementSatisfied(
        RefinementAnnotation? refinement,
        object? value,
        TextSpan span,
        string sourceName,
        string sourceText,
        string owner)
        => EnsureRefinementSatisfiedAsync(
                refinement,
                value,
                span,
                sourceName,
                sourceText,
                owner,
                CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();

    private bool TryResolveRefinementTypeForAnnotation(string typeName, out RefinementTypeDefinition definition)
    {
        if (TryGetRefinementType(typeName, out definition))
        {
            return true;
        }

        if (!TrySplitGenericTypeName(typeName, out var genericName, out var typeArguments) ||
            !TryGetRefinementType(genericName, out var genericDefinition))
        {
            definition = null!;
            return false;
        }

        if (genericDefinition.TypeParameters.Count == 0 ||
            genericDefinition.TypeParameters.Count != typeArguments.Count)
        {
            definition = null!;
            return false;
        }

        var substitutions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < genericDefinition.TypeParameters.Count; index++)
        {
            substitutions[genericDefinition.TypeParameters[index]] = typeArguments[index];
        }

        definition = new RefinementTypeDefinition(
            Name: typeName,
            TypeParameters: Array.Empty<string>(),
            BaseTypeName: SubstituteTypeParametersInTypeName(genericDefinition.BaseTypeName, substitutions),
            Refinement: SpecializeRefinementAnnotation(genericDefinition, typeName, substitutions),
            SourceName: genericDefinition.SourceName,
            SourceText: genericDefinition.SourceText,
            Modifier: genericDefinition.Modifier,
            Span: genericDefinition.Span);
        return true;
    }

    private RefinementAnnotation? SpecializeRefinementAnnotation(
        RefinementTypeDefinition genericDefinition,
        string closedTypeName,
        IReadOnlyDictionary<string, string> substitutions)
    {
        if (genericDefinition.Refinement is null)
        {
            return null;
        }

        var syntheticSourceName = $"{genericDefinition.SourceName}<{closedTypeName}>";
        var builder = new StringBuilder();
        builder.AppendLine("type __Ref = any {");
        foreach (var clause in genericDefinition.Refinement.Clauses)
        {
            switch (clause)
            {
                case RefinementWhereClause whereClause:
                    builder.Append("    where ")
                        .AppendLine(SubstituteTypeParametersInText(
                            ExtractSourceSnippet(genericDefinition.Refinement.SourceText, whereClause.Predicate.Span),
                            substitutions));
                    break;
                case RefinementCoerceClause { Guard: { } guard, Coercer: var coercer }:
                    builder.Append("    if ")
                        .Append(SubstituteTypeParametersInText(
                            ExtractSourceSnippet(genericDefinition.Refinement.SourceText, guard.Span),
                            substitutions))
                        .Append(" coerce ")
                        .AppendLine(SubstituteTypeParametersInText(
                            ExtractSourceSnippet(genericDefinition.Refinement.SourceText, coercer.Span),
                            substitutions));
                    break;
                case RefinementCoerceClause { Guard: null, Coercer: var coercer }:
                    builder.Append("    coerce ")
                        .AppendLine(SubstituteTypeParametersInText(
                            ExtractSourceSnippet(genericDefinition.Refinement.SourceText, coercer.Span),
                            substitutions));
                    break;
            }
        }
        builder.Append('}');
        var syntheticSourceText = builder.ToString();

        var parseResult = ToshParser.Parse(syntheticSourceText, syntheticSourceName);
        if (parseResult.Diagnostics.Count > 0 ||
            parseResult.Statement is not TypeAliasStatementSyntax { Refinement: { } specializedRefinement })
        {
            var diagnostic = parseResult.Diagnostics.FirstOrDefault();
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.refinement_specialization_failed",
                Title: $"Refinement alias '{genericDefinition.Name}' could not be specialized for '{closedTypeName}'.",
                SourceName: genericDefinition.SourceName,
                SourceText: genericDefinition.SourceText,
                Span: genericDefinition.Span,
                Label: "this generic refinement alias could not be instantiated",
                Help: diagnostic?.Title));
        }

        return CreateRefinementAnnotation(syntheticSourceName, syntheticSourceText, specializedRefinement)! with
        {
            CapturedScopes = genericDefinition.Refinement.CapturedScopes,
        };
    }

    private static ArgumentSyntax GetPrimaryRefinementPredicate(RefinementAnnotation refinement)
        => refinement.Clauses.OfType<RefinementWhereClause>().First().Predicate;

    private static bool TryGetRefinementSnippet(string sourceText, TextSpan span, out string snippet)
    {
        if (span.Start >= 0 && span.Start + span.Length <= sourceText.Length)
        {
            snippet = sourceText.Substring(span.Start, span.Length).Trim();
            return true;
        }

        snippet = string.Empty;
        return false;
    }

    /// <summary>
    /// Phase 4.5 — substitute type-parameter references inside a
    /// constraint annotation. The currently-binding parameter
    /// (<paramref name="currentBindingName"/>) is replaced with
    /// <paramref name="currentBindingType"/>; other type parameters
    /// of <paramref name="target"/> are replaced with whatever
    /// <paramref name="typeBindings"/> holds for them.
    /// </summary>
    private static string SubstituteTypeParametersInAnnotation(
        string annotation,
        GenericInferenceTarget target,
        Dictionary<string, Type> typeBindings,
        string currentBindingName,
        Type currentBindingType)
    {
        if (annotation.IndexOf('<') < 0) return annotation;

        var sb = new StringBuilder();
        var i = 0;
        while (i < annotation.Length)
        {
            // Greedy identifier scan.
            if (char.IsLetter(annotation[i]) || annotation[i] == '_')
            {
                var start = i;
                while (i < annotation.Length && (char.IsLetterOrDigit(annotation[i]) || annotation[i] == '_'))
                {
                    i++;
                }
                var ident = annotation.Substring(start, i - start);
                if (string.Equals(ident, currentBindingName, StringComparison.Ordinal))
                {
                    sb.Append(currentBindingType.FullName ?? currentBindingType.Name);
                }
                else if (target.TypeParameters.Contains(ident, StringComparer.Ordinal)
                         && typeBindings.TryGetValue(ident, out var bound))
                {
                    sb.Append(bound.FullName ?? bound.Name);
                }
                else
                {
                    sb.Append(ident);
                }
                continue;
            }
            sb.Append(annotation[i]);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Phase 3.2 — annotation-vs-annotation unification. Used to
    /// seed type-parameter bindings from a target type (LHS of
    /// `var x: T = …`) against a function's declared return type.
    /// Best-effort: silently no-ops on shape mismatches so a wrong
    /// guess at the call site doesn't poison the binding table.
    /// </summary>
    private void UnifyAnnotationWithAnnotation(
        GenericInferenceTarget target,
        string returnAnnotation,
        string targetAnnotation,
        Dictionary<string, Type> typeBindings)
    {
        returnAnnotation = returnAnnotation.Trim();
        targetAnnotation = targetAnnotation.Trim();
        if (returnAnnotation.Length == 0 || targetAnnotation.Length == 0) return;

        // Bare type-parameter reference: resolve target as a CLR type
        // and bind. Unresolvable target types are silently ignored.
        if (target.TypeParameters.Contains(returnAnnotation, StringComparer.Ordinal))
        {
            if (typeBindings.ContainsKey(returnAnnotation)) return;
            var resolved = TryResolveTypeName(targetAnnotation);
            if (resolved is not null)
            {
                typeBindings[returnAnnotation] = resolved;
            }
            return;
        }

        // Decompose `Head<args>` on both sides; heads must match
        // (case-insensitive).
        var rLt = returnAnnotation.IndexOf('<');
        var rGt = returnAnnotation.LastIndexOf('>');
        var tLt = targetAnnotation.IndexOf('<');
        var tGt = targetAnnotation.LastIndexOf('>');
        if (rLt <= 0 || rGt != returnAnnotation.Length - 1) return;
        if (tLt <= 0 || tGt != targetAnnotation.Length - 1) return;

        var rHead = returnAnnotation.Substring(0, rLt).Trim();
        var tHead = targetAnnotation.Substring(0, tLt).Trim();
        if (!string.Equals(rHead, tHead, StringComparison.OrdinalIgnoreCase)) return;

        var rArgs = SplitTopLevelCommas(returnAnnotation.Substring(rLt + 1, rGt - rLt - 1));
        var tArgs = SplitTopLevelCommas(targetAnnotation.Substring(tLt + 1, tGt - tLt - 1));
        if (rArgs.Count != tArgs.Count) return;
        for (var i = 0; i < rArgs.Count; i++)
        {
            UnifyAnnotationWithAnnotation(target, rArgs[i], tArgs[i], typeBindings);
        }
    }

    /// <summary>
    /// Recursive driver: parse one annotation node, match its head
    /// against the value's runtime shape, then recurse into nested
    /// type arguments using the value's element / key / value types.
    /// </summary>
    private void UnifyAnnotationWithValue(
        GenericInferenceTarget target,
        string parameterName,
        string annotation,
        object? value,
        CommandContext context,
        int argumentIndex,
        Dictionary<string, Type> typeBindings)
    {
        annotation = annotation.Trim();
        if (annotation.Length == 0 || value is null) return;

        // Bare type-parameter reference: bind / validate directly.
        if (target.TypeParameters.Contains(annotation, StringComparer.Ordinal))
        {
            BindOrValidateTypeParameter(target, parameterName, annotation, value, context, argumentIndex, typeBindings);
            return;
        }

        // Decompose `Head<arg1, arg2, ...>`.
        var lt = annotation.IndexOf('<');
        var gt = annotation.LastIndexOf('>');
        if (lt <= 0 || gt != annotation.Length - 1) return; // no nested args — nothing to infer
        var head = annotation.Substring(0, lt).Trim();
        var inner = annotation.Substring(lt + 1, gt - lt - 1);
        var args = SplitTopLevelCommas(inner);
        if (args.Count == 0) return;

        // Unify each annotation arg with the matching shape from
        // the runtime value. Heads we recognise: list, array, dict,
        // map, tuple. Unknown heads fall back to a single-arg
        // element-type peek if the value is enumerable.
        var headLower = head.ToLowerInvariant();
        switch (headLower)
        {
            case "list":
            case "array":
            case "ienumerable":
            case "icollection":
            case "ireadonlylist":
            case "ireadonlycollection":
                if (args.Count == 1 && TryGetElementType(value, out var elemType, out var sample))
                {
                    UnifyShapeArg(target, parameterName, args[0].Trim(), sample, elemType, context, argumentIndex, typeBindings);
                }
                break;

            case "dict":
            case "dictionary":
            case "map":
            case "idictionary":
            case "ireadonlydictionary":
                if (args.Count == 2 && TryGetDictionaryKVTypes(value, out var keyType, out var valType, out var keySample, out var valSample))
                {
                    UnifyShapeArg(target, parameterName, args[0].Trim(), keySample, keyType, context, argumentIndex, typeBindings);
                    UnifyShapeArg(target, parameterName, args[1].Trim(), valSample, valType, context, argumentIndex, typeBindings);
                }
                break;

            default:
                // Generic CLR type: try to read its bound type-args
                // from the runtime instance's GetType() and unify
                // pointwise.
                var clrType = value.GetType();
                if (clrType.IsGenericType)
                {
                    var clrArgs = clrType.GetGenericArguments();
                    var pairs = Math.Min(clrArgs.Length, args.Count);
                    for (var i = 0; i < pairs; i++)
                    {
                        UnifyShapeArg(target, parameterName, args[i].Trim(), null, clrArgs[i], context, argumentIndex, typeBindings);
                    }
                }
                break;
        }
    }

    private sealed record AnnotationRefinementFailure(object? Value, RefinementAnnotation? Refinement);
}
