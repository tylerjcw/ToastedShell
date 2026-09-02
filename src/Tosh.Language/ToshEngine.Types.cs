using Tosh.Runtime;
using Tosh.Language.Parsing;

namespace Tosh.Language;

/// <summary>
/// User-defined types: executing a `class`, `interface`, `enum` or `trait` declaration,
/// the class execution frame, and class-level operator and pipeline dispatch.
///
/// Moved out of ToshEngine.cs by `TOAST-0005`. Every member moved **verbatim**.
///
/// `IsNumericEnumUnderlyingType` came along after checking rather than guessing: it has
/// exactly one caller, inside `EvaluateEnumDefinitionAsync`, so it is enum-declaration
/// code rather than a general numeric-type predicate. Several `Enumerate*` and
/// `Enumerator` members were left behind for the opposite reason — they match a grep
/// for "Enum" and have nothing to do with enums.
/// </summary>
public sealed partial class ToshEngine
{

    private async IAsyncEnumerable<object?> EvaluateClassDefinitionAsync(
        string sourceName,
        string sourceText,
        ClassDefinitionStatementSyntax @class,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, @class.Name, @class.Span, "reserved runtime namespace");

        var duplicateProperties = @class.Members
            .OfType<ClassPropertyMemberSyntax>()
            .GroupBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicateProperties is not null)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.duplicate_class_property",
                Title: $"Class '{@class.Name}' defines property '{duplicateProperties.Key}' more than once.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: duplicateProperties.First().Span,
                Label: $"'{duplicateProperties.Key}' is declared multiple times"));
        }

        foreach (var parameter in @class.PrimaryConstructorParameters)
        {
            EnsureBindingNameIsNotReserved(sourceName, sourceText, parameter.Name, parameter.Span, "reserved runtime namespace");
        }

        foreach (var constructorParameter in @class.Members
                     .OfType<ClassConstructorMemberSyntax>()
                     .SelectMany(member => member.Parameters))
        {
            EnsureBindingNameIsNotReserved(sourceName, sourceText, constructorParameter.Name, constructorParameter.Span, "reserved runtime namespace");
        }

        foreach (var methodParameter in @class.Members
                     .OfType<ClassMethodMemberSyntax>()
                     .SelectMany(member => member.Method.Parameters))
        {
            EnsureBindingNameIsNotReserved(sourceName, sourceText, methodParameter.Name, methodParameter.Span, "reserved runtime namespace");
        }

        var classTypeParams = @class.TypeParameters;

        var runtimeProperties = @class.Members
            .OfType<ClassPropertyMemberSyntax>()
            .Select(property => new ToshClassPropertyDefinition(
                property.Name,
                // Keep the original type-parameter name (e.g. 'T1') so
                // the runtime can substitute it against the instance's
                // generic bindings on each access. Concrete CLR-style
                // types are passed through unchanged.
                property.TypeName,
                property.Initializer,
                property.GetterBody,
                property.SetterBody,
                property.IsShy,
                property.IsStatic || @class.IsHermit,  // hermit classes make all members implicitly shared
                property.IsFixed || @class.IsStrict,  // strict classes make all properties fixed
                property.IsVital,
                property.IsGuarded,
                property.IsLazy,
                property.IsFading,
                property.IsLocal,
                property.IsAbstract,
                property.Span,
                CreateRefinementAnnotation(sourceName, sourceText, property.Refinement),
                property.DocComment))
            .ToArray();

        var runtimeMethods = @class.Members
            .OfType<ClassMethodMemberSyntax>()
            .Select(method =>
            {
                // Phase 3.4 — method-level type parameters. Combine
                // the class's type-parameter names with the method's
                // own so erasure removes both before annotation
                // resolution.
                var methodTypeParams = method.Method.TypeParameters;
                IReadOnlyList<string>? combinedTypeParams = classTypeParams;
                if (methodTypeParams is { Count: > 0 })
                {
                    combinedTypeParams = (classTypeParams is { Count: > 0 })
                        ? classTypeParams.Concat(methodTypeParams).ToArray()
                        : methodTypeParams;
                }
                var methodConstraints = method.Method.TypeParameterConstraints?
                    .Select(c => new ToshTypeParameterConstraint(c.TypeParameter, c.ConstraintNames))
                    .ToArray();

                return new ToshClassMethodDefinition(
                method.Method.Name,
                method.Method.Parameters
                    .Select(parameter => CreateParameterDefinition(parameter, sourceName, sourceText, combinedTypeParams))
                    .ToArray(),
                EraseTypeParameter(method.Method.ReturnTypeName, combinedTypeParams),
                method.Method.Body,
                method.IsStatic || @class.IsHermit,  // hermit classes make all members implicitly shared
                method.IsShy,
                method.IsAbstract,
                method.IsOverride,
                method.IsGuarded,
                method.IsFading,
                method.IsLocal,
                method.IsRaw,
                sourceName,
                sourceText,
                method.Span,
                CaptureVisibleScopes(),
                // Preserve the un-erased return-type annotation so a generic
                // class can substitute T against the instance's bindings at
                // call time (see ToshClassDefinition.ExecuteMethodBlock).
                RawReturnTypeName: method.Method.ReturnTypeName,
                TypeParameters: methodTypeParams,
                TypeParameterConstraints: methodConstraints,
                Documentation: method.Method.DocComment);
            })
            .ToArray();

        var runtimeConstructors = @class.Members
            .OfType<ClassConstructorMemberSyntax>()
            .Select(constructor => new ToshClassConstructorDefinition(
                constructor.Parameters
                    .Select(parameter => CreateParameterDefinition(parameter, sourceName, sourceText, classTypeParams))
                    .ToArray(),
                constructor.Body,
                sourceName,
                sourceText,
                constructor.Span,
                CaptureVisibleScopes()))
            .ToArray();

        var typeParameterConstraints = @class.TypeParameterConstraints?
            .Select(c => new ToshTypeParameterConstraint(c.TypeParameter, c.ConstraintNames))
            .ToArray();

        var definition = new ToshClassDefinition(
            this,
            @class.Name,
            @class.PrimaryConstructorParameters
                .Select(parameter => CreateParameterDefinition(parameter, sourceName, sourceText, classTypeParams))
                .ToArray(),
            runtimeProperties,
            runtimeMethods,
            runtimeConstructors,
            sourceName,
            sourceText,
            @class.Span,
            CaptureVisibleScopes(),
            typeParameters: classTypeParams,
            typeParameterConstraints: typeParameterConstraints,
            documentation: @class.DocComment);

        // Handle partial class merging: if this is a partial class and one already exists, merge members
        if (@class.IsPartial && TryGetNamedType(@class.Name, out var existingType) && existingType is ToshClassDefinition existingDef)
        {
            if (!existingDef.IsPartial)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.partial_mismatch",
                    Title: $"Cannot extend class '{@class.Name}' as partial: the original class was not declared as partial.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: @class.Span,
                    Label: "both declarations must be partial"));
            }

            existingDef.MergePartial(runtimeProperties, runtimeMethods, runtimeConstructors);

            // After the merge, not before: a later part can contribute the constructor that
            // collides with an earlier part's, and neither part is wrong on its own.
            existingDef.ValidateConstructorSignatures();

            await BindNativeClassMembersAsync(sourceName, sourceText, @class, existingDef, cancellationToken);

            // Declared again rather than returning early, so a file contributing
            // a partial exports the name it contributed to. Same object, so the
            // two declarations cannot diverge.
            DeclareType(@class.Name, existingDef, @class.Modifier, sourceName, sourceText, @class.Span);
            yield break;
        }

        // Capture the module this class is being declared in, so an unqualified
        // annotation inside its body still resolves when a member runs later and
        // the module scope is no longer on the stack.
        foreach (var scope in _scopes)
        {
            if (scope.Exports is { } moduleExports)
            {
                definition.DeclaringExports = moduleExports;
                break;
            }
        }

        // Before DeclareType, so a class the resolver could never construct never enters scope.
        definition.ValidateConstructorSignatures();

        await BindNativeClassMembersAsync(sourceName, sourceText, @class, definition, cancellationToken);

        DeclareType(@class.Name, definition, @class.Modifier, sourceName, sourceText, @class.Span);

        // Nested types are evaluated after the class is in scope, so one may refer to the class
        // that declares it.
        await EvaluateNestedTypeMembersAsync(sourceName, sourceText, @class, definition, cancellationToken);

        definition.IsSealed = @class.IsSealed;
        definition.IsAbstract = @class.IsAbstract;
        definition.IsHermit = @class.IsHermit;
        definition.IsStrict = @class.IsStrict;
        definition.IsPartial = @class.IsPartial;

        // Validate hermit (static) classes: constructors not allowed (members are auto-shared)
        if (definition.IsHermit)
        {
            if (runtimeConstructors.Length > 0)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.hermit_has_constructor",
                    Title: $"Hermit class '{@class.Name}' cannot have constructors.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: @class.Span,
                    Label: "hermit classes cannot be instantiated"));
            }
        }

        // Resolve base class
        if (@class.BaseClassName is not null)
        {
            // Preserve the distinction between no constructor initializer and
            // an explicitly empty `extends Base()` initializer for both TōSh
            // and CLR base classes.
            if (@class.BaseConstructorArgs is not null)
            {
                definition.BaseConstructorArgs = @class.BaseConstructorArgs;
            }

            if (TryGetNamedType(@class.BaseClassName, out var baseType) && baseType is ToshClassDefinition baseClassDef)
            {
                if (baseClassDef.IsSealed)
                {
                    throw ToshDiagnosticException.Create(new ToshDiagnostic(
                        Code: "tosh.runtime.extend_sealed_class",
                        Title: $"Class '{@class.Name}' cannot extend sealed class '{@class.BaseClassName}'.",
                        SourceName: sourceName,
                        SourceText: sourceText,
                        Span: @class.Span,
                        Label: $"'{@class.BaseClassName}' is marked sealed and cannot be extended"));
                }

                definition.BaseClass = baseClassDef;

                // Store extends clause type-arguments and validate arity
                if (@class.BaseTypeArguments is not null)
                {
                    if (@class.BaseTypeArguments.Count != baseClassDef.TypeParameterNames.Count)
                    {
                        throw ToshDiagnosticException.Create(new ToshDiagnostic(
                            Code: "tosh.runtime.base_type_argument_arity",
                            Title: $"Class '{@class.Name}' supplies {@class.BaseTypeArguments.Count} type argument(s) to base class '{baseClassDef.Name}', which expects {baseClassDef.TypeParameterNames.Count}.",
                            SourceName: sourceName,
                            SourceText: sourceText,
                            Span: @class.Span,
                            Label: $"'{baseClassDef.Name}' has {baseClassDef.TypeParameterNames.Count} type parameter(s): <{string.Join(", ", baseClassDef.TypeParameterNames)}>"));
                    }

                    definition.BaseTypeArguments = @class.BaseTypeArguments;

                    // Eagerly resolve concrete type-argument strings; entries
                    // that are themselves child type-parameters are left null
                    // (they get forwarded at instance construction time).
                    var childTypeParams = definition.TypeParameterNames;
                    var resolved = new Type?[@class.BaseTypeArguments.Count];
                    for (int i = 0; i < @class.BaseTypeArguments.Count; i++)
                    {
                        var argString = @class.BaseTypeArguments[i];
                        if (childTypeParams.Contains(argString, StringComparer.OrdinalIgnoreCase))
                        {
                            resolved[i] = null;
                        }
                        else
                        {
                            resolved[i] = ResolveTypeName(argString);
                        }
                    }
                    definition.BaseTypeArgumentsResolved = resolved;
                }
                else if (baseClassDef.TypeParameterNames.Count > 0)
                {
                    throw ToshDiagnosticException.Create(new ToshDiagnostic(
                        Code: "tosh.runtime.base_type_argument_missing",
                        Title: $"Class '{@class.Name}' extends generic class '{baseClassDef.Name}' without supplying type arguments.",
                        SourceName: sourceName,
                        SourceText: sourceText,
                        Span: @class.Span,
                        Label: $"write 'extends {baseClassDef.Name}<{string.Join(", ", baseClassDef.TypeParameterNames)}>'"));
                }
            }
            else
            {
                // Try resolving as a CLR type
                var clrType = ResolveTypeName(@class.BaseClassName);
                if (clrType is not null)
                {
                    definition.ClrBaseType = clrType;
                }
                else
                {
                    throw ToshDiagnosticException.Create(new ToshDiagnostic(
                        Code: "tosh.runtime.unknown_base_class",
                        Title: $"Class '{@class.Name}' extends unknown class '{@class.BaseClassName}'.",
                        SourceName: sourceName,
                        SourceText: sourceText,
                        Span: @class.Span,
                        Label: $"'{@class.BaseClassName}' is not a known class"));
                }
            }
        }

        // Validate implemented interfaces
        if (@class.ImplementedInterfaces is { Count: > 0 })
        {
            foreach (var ifaceName in @class.ImplementedInterfaces)
            {
                // The fulfills clause may carry generic type arguments
                // (e.g. 'fulfills IPoint<int>'). Type definitions are
                // registered by their bare name, so look up using the
                // unparameterised head while keeping the full reference
                // string for diagnostics.
                var lookupName = StripGenericTypeArguments(ifaceName);
                if (!TryGetNamedType(lookupName, out var namedType) || namedType is not ToshInterfaceDefinition ifaceDefinition)
                {
                    throw ToshDiagnosticException.Create(new ToshDiagnostic(
                        Code: "tosh.runtime.unknown_interface",
                        Title: $"Class '{@class.Name}' fulfills unknown interface '{ifaceName}'.",
                        SourceName: sourceName,
                        SourceText: sourceText,
                        Span: @class.Span,
                        Label: $"'{ifaceName}' is not a known interface"));
                }

                // Validate type-argument arity / constraints when the
                // interface is generic and the fulfills clause carries
                // type arguments.
                ValidateInterfaceTypeArguments(
                    sourceName,
                    sourceText,
                    @class,
                    ifaceDefinition,
                    ifaceName);

                // `TOAST-0020`. The same rule traits get, decided 2026-08-17 — interfaces
                // are methods-only, so this is the whole of it for them. They had the
                // identical gap and sit one block away; leaving them out would have left two
                // neighbouring constructs behaving differently for no stated reason.
                foreach (var signature in ifaceDefinition.Methods)
                {
                    ThrowOnContractTypeMismatch(
                        sourceName, sourceText, @class, ifaceName, "interface", signature.Name,
                        ResolveMemberTypeMismatch(definition, signature.Name, signature.Parameters, signature.ReturnTypeName));
                }

                var missing = ifaceDefinition.GetMissingMethods(definition);
                if (missing.Count > 0)
                {
                    throw ToshDiagnosticException.Create(new ToshDiagnostic(
                        Code: "tosh.runtime.missing_interface_methods",
                        Title: $"Class '{@class.Name}' does not implement all methods of interface '{ifaceName}'. Missing: {string.Join(", ", missing)}.",
                        SourceName: sourceName,
                        SourceText: sourceText,
                        Span: @class.Span,
                        Label: $"missing: {string.Join(", ", missing)}"));
                }
            }

            definition.ImplementedInterfaces = @class.ImplementedInterfaces
                .Select(name => TryGetNamedType(StripGenericTypeArguments(name), out var t) && t is ToshInterfaceDefinition iface ? iface : null)
                .Where(i => i is not null)
                .ToArray()!;
        }

        // Validate used traits and inject default methods/properties
        if (@class.UsedTraits is { Count: > 0 })
        {
            foreach (var traitName in @class.UsedTraits)
            {
                if (!TryGetNamedType(traitName, out var namedType) || namedType is not ToshTraitDefinition traitDefinition)
                {
                    throw ToshDiagnosticException.Create(new ToshDiagnostic(
                        Code: "tosh.runtime.unknown_trait",
                        Title: $"Class '{@class.Name}' uses unknown trait '{traitName}'.",
                        SourceName: sourceName,
                        SourceText: sourceText,
                        Span: @class.Span,
                        Label: $"'{traitName}' is not a known trait"));
                }

                // Check required methods (those without default bodies)
                var missingMethods = traitDefinition.GetMissingMethods(definition);
                if (missingMethods.Count > 0)
                {
                    throw ToshDiagnosticException.Create(new ToshDiagnostic(
                        Code: "tosh.runtime.missing_trait_methods",
                        Title: $"Class '{@class.Name}' does not implement required methods from trait '{traitName}'. Missing: {string.Join(", ", missingMethods)}.",
                        SourceName: sourceName,
                        SourceText: sourceText,
                        Span: @class.Span,
                        Label: $"missing: {string.Join(", ", missingMethods)}"));
                }

                // `TOAST-0020`. A trait declares what its members give back and take, and
                // nothing checked it — a class could satisfy `render() -> string` with an
                // implementation returning `int`, so the trait was a naming convention
                // rather than a contract.
                //
                // Decided 2026-08-17: **covariant returns, exact parameters**, reported
                // here. Here rather than in `TypeChecker` because the rule needs a subtype
                // relation, and the checker holds annotation *names* while the engine holds
                // the declarations — this is the one place that already has both the trait
                // and the class in hand.
                foreach (var method in traitDefinition.Methods)
                {
                    ThrowOnContractTypeMismatch(
                        sourceName, sourceText, @class, traitName, "trait", method.Name,
                        ResolveMemberTypeMismatch(definition, method.Name, method.Parameters, method.ReturnTypeName));
                }

                // Properties are checked **invariantly** — see `ResolvePropertyTypeMismatch`
                // for why a writable member cannot narrow the way a return can.
                foreach (var property in traitDefinition.Properties)
                {
                    ThrowOnContractTypeMismatch(
                        sourceName, sourceText, @class, traitName, "trait", property.Name,
                        ResolvePropertyTypeMismatch(definition, property));
                }

                // Check required properties (those without default values)
                var missingProps = traitDefinition.GetMissingProperties(definition);
                if (missingProps.Count > 0)
                {
                    throw ToshDiagnosticException.Create(new ToshDiagnostic(
                        Code: "tosh.runtime.missing_trait_properties",
                        Title: $"Class '{@class.Name}' does not implement required properties from trait '{traitName}'. Missing: {string.Join(", ", missingProps)}.",
                        SourceName: sourceName,
                        SourceText: sourceText,
                        Span: @class.Span,
                        Label: $"missing: {string.Join(", ", missingProps)}"));
                }

                // Inject default methods that the class doesn't already define
                foreach (var traitMethod in traitDefinition.Methods.Where(m => m.HasDefaultBody))
                {
                    if (!definition.Methods.Any(m => string.Equals(m.Name, traitMethod.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        definition.AddMethod(new ToshClassMethodDefinition(
                            traitMethod.Name,
                            traitMethod.Parameters,
                            traitMethod.ReturnTypeName,
                            traitMethod.DefaultBody!,
                            IsStatic: false,
                            IsShy: false,
                            IsAbstract: false,
                            IsOverride: false,
                            IsGuarded: false,
                            IsFading: false,
                            IsLocal: false,
                            IsRaw: false,
                            sourceName,
                            sourceText,
                            @class.Span,
                            CapturedScopes: CaptureVisibleScopes()));
                    }
                }

                // Inject default property values for properties the class doesn't define
                foreach (var traitProp in traitDefinition.Properties.Where(p => p.DefaultValue is not null))
                {
                    if (!definition.Properties.Any(p => string.Equals(p.Name, traitProp.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        definition.AddProperty(new ToshClassPropertyDefinition(
                            traitProp.Name,
                            traitProp.TypeName,
                            traitProp.DefaultValue,
                            GetterBody: null,
                            SetterBody: null,
                            IsShy: false,
                            IsStatic: false,
                            IsFixed: false,
                            IsVital: false,
                            IsGuarded: false,
                            IsLazy: false,
                            IsFading: false,
                            IsLocal: false,
                            IsAbstract: false,
                            @class.Span));
                    }
                }
            }

            definition.UsedTraits = @class.UsedTraits
                .Select(name => TryGetNamedType(name, out var t) && t is ToshTraitDefinition trait ? trait : null)
                .Where(t => t is not null)
                .ToArray()!;
        }

        // Validate that non-abstract classes implement all hollow (abstract) methods from parent
        if (!definition.IsAbstract && definition.BaseClass is { } parentClass)
        {
            var unimplemented = GetUnimplementedAbstractMethods(parentClass, definition);
            if (unimplemented.Count > 0)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.missing_hollow_methods",
                    Title: $"Class '{@class.Name}' must implement hollow methods from '{parentClass.Name}': {string.Join(", ", unimplemented)}.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: @class.Span,
                    Label: $"missing hollow methods: {string.Join(", ", unimplemented)}"));
            }

            var unimplementedProps = GetUnimplementedAbstractProperties(parentClass, definition);
            if (unimplementedProps.Count > 0)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.missing_hollow_properties",
                    Title: $"Class '{@class.Name}' must implement hollow properties from '{parentClass.Name}': {string.Join(", ", unimplementedProps)}.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: @class.Span,
                    Label: $"missing hollow properties: {string.Join(", ", unimplementedProps)}"));
            }
        }

        // Validate overrule methods have a matching parent method
        foreach (var method in runtimeMethods.Where(m => m.IsOverride))
        {
            if (definition.BaseClass is null || !HasMethodInHierarchy(definition.BaseClass, method.Name))
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.overrule_no_base_method",
                    Title: $"Method '{method.Name}' in class '{@class.Name}' is marked 'overrule' but no parent class defines '{method.Name}'.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: method.Span,
                    Label: $"no '{method.Name}' found in parent hierarchy to overrule"));
            }
        }

        // Validate that methods shadowing a parent method are marked 'overrule'
        if (definition.BaseClass is not null)
        {
            foreach (var method in runtimeMethods.Where(m => !m.IsOverride && !m.IsAbstract && !m.IsStatic))
            {
                // Matched on the whole signature rather than the name alone. A same-named method
                // with different parameters is an *overload*, not an override, and demanding
                // `overrule` for it made an inherited overload set impossible to extend: a class
                // whose base declared `func f(a: int)` could not declare `func f(a: string)`,
                // nor even `func f(a: int, b: int)`, without claiming to override something it
                // does not.
                if (OverridesMethodInHierarchy(definition.BaseClass, method))
                {
                    throw ToshDiagnosticException.Create(new ToshDiagnostic(
                        Code: "tosh.runtime.missing_overrule",
                        Title: $"Method '{method.Name}' in class '{@class.Name}' shadows a parent method but is not marked 'overrule'.",
                        SourceName: sourceName,
                        SourceText: sourceText,
                        Span: method.Span,
                        Label: $"add 'overrule' to override '{method.Name}'"));
                }
            }
        }

        // Initialize static property values
        foreach (var prop in runtimeProperties.Where(p => p.IsStatic && p.Initializer is not null))
        {
            var values = await AsyncEnumerableExtensions.ToListAsync(
                EvaluatePipelineAsync(sourceName, sourceText, prop.Initializer!, cancellationToken, outputIsCaptured: true),
                cancellationToken);
            definition.InitializeStaticMember(prop.Name, values.Count == 1 ? values[0] : values);
        }

        yield break;
    }

    private async IAsyncEnumerable<object?> EvaluateInterfaceDefinitionAsync(
        string sourceName,
        string sourceText,
        InterfaceDefinitionStatementSyntax @interface,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, @interface.Name, @interface.Span, "reserved runtime namespace");

        var methods = @interface.Methods
            .Select(m => new InterfaceMethodSignature(
                m.Name,
                m.Parameters
                    .Select(p => CreateParameterDefinition(p, sourceName, sourceText))
                    .ToArray(),
                m.ReturnTypeName))
            .ToArray();

        var definition = new ToshInterfaceDefinition(
            @interface.Name,
            methods,
            sourceName,
            sourceText,
            @interface.Span,
            typeParameterNames: @interface.TypeParameters,
            typeParameterConstraints: @interface.TypeParameterConstraints,
            typeParameterVariances: @interface.TypeParameterVariances);

        definition.Documentation = @interface.DocComment;
        DeclareType(@interface.Name, definition, @interface.Modifier, sourceName, sourceText, @interface.Span);
        yield break;
    }

    private async IAsyncEnumerable<object?> EvaluateEnumDefinitionAsync(
        string sourceName,
        string sourceText,
        EnumDefinitionStatementSyntax @enum,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, @enum.Name, @enum.Span, "reserved runtime namespace");

        var underlyingType = string.IsNullOrWhiteSpace(@enum.UnderlyingTypeName)
            ? typeof(int)
            : ResolveTypeName(@enum.UnderlyingTypeName!)
                ?? throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.unknown_enum_underlying_type",
                    Title: $"Enum '{@enum.Name}' uses unknown underlying type '{@enum.UnderlyingTypeName}'.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: @enum.Span,
                    Label: $"the type '{@enum.UnderlyingTypeName}' could not be resolved"));

        var members = new List<ToshEnumValue>();
        long nextNumericValue = 0;
        var canAutoIncrement = IsNumericEnumUnderlyingType(underlyingType);

        foreach (var member in @enum.Members)
        {
            object? rawValue;

            if (member.Value is null)
            {
                if (!canAutoIncrement)
                {
                    throw ToshDiagnosticException.Create(new ToshDiagnostic(
                        Code: "tosh.runtime.enum_member_value_required",
                        Title: $"Enum member '{@enum.Name}.{member.Name}' requires an explicit value.",
                        SourceName: sourceName,
                        SourceText: sourceText,
                        Span: member.Span,
                        Label: $"'{underlyingType.Name}' values cannot be auto-incremented"));
                }

                rawValue = Convert.ChangeType(nextNumericValue, underlyingType);
            }
            else if (await TryEvaluateRawExpressionPipelineAsync(sourceName, sourceText, member.Value, cancellationToken) is { Matched: true } raw)
            {
                rawValue = raw.Value;
            }
            else
            {
                var values = await AsyncEnumerableExtensions.ToListAsync(
                    EvaluatePipelineAsync(sourceName, sourceText, member.Value, cancellationToken, outputIsCaptured: true),
                    cancellationToken);
                rawValue = values.Count switch
                {
                    0 => null,
                    1 => values[0],
                    _ => throw ToshDiagnosticException.Create(new ToshDiagnostic(
                        Code: "tosh.runtime.enum_member_requires_single_value",
                        Title: $"Enum member '{@enum.Name}.{member.Name}' must resolve to exactly one value.",
                        SourceName: sourceName,
                        SourceText: sourceText,
                        Span: member.Span,
                        Label: "this enum member initializer produced multiple values")),
                };
            }

            if (!TypeConversion.TryConvert(rawValue, underlyingType, out var converted))
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.enum_member_conversion_failed",
                    Title: $"Enum member '{@enum.Name}.{member.Name}' could not be converted to '{underlyingType.Name}'.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: member.Span,
                    Label: $"the value does not match '{underlyingType.Name}'"));
            }

            members.Add(new ToshEnumValue(default!, member.Name, converted));

            if (canAutoIncrement)
            {
                nextNumericValue = Convert.ToInt64(converted, System.Globalization.CultureInfo.InvariantCulture) + 1;
            }
        }

        var definition = new ToshEnumDefinition(
            @enum.Name,
            @enum.UnderlyingTypeName,
            underlyingType,
            members,
            sourceName,
            sourceText,
            @enum.Span);

        // `TS-P3-14`. Set before the members are built, not after: each member
        // captures *this* definition object, and the second construction below
        // replaces the registered one without changing what the members point at.
        // Assigning only to the second left every member reporting a descriptor
        // whose `IsFlags` was false, so combining them produced a number.
        definition.IsFlags = @enum.IsFlags;

        var fixedMembers = definition.Members
            .Select(member => new ToshEnumValue(definition, member.Name, member.UnderlyingValue))
            .ToArray();
        definition = new ToshEnumDefinition(
            @enum.Name,
            @enum.UnderlyingTypeName,
            underlyingType,
            fixedMembers,
            sourceName,
            sourceText,
            @enum.Span);

        definition.Documentation = @enum.DocComment;
        definition.IsFlags = @enum.IsFlags;
        DeclareType(@enum.Name, definition, @enum.Modifier, sourceName, sourceText, @enum.Span);
        yield break;
    }

    private async IAsyncEnumerable<object?> EvaluateTraitDefinitionAsync(
        string sourceName,
        string sourceText,
        TraitDefinitionStatementSyntax trait,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, trait.Name, trait.Span, "reserved runtime namespace");

        var methods = trait.Methods
            .Select(m => new TraitMethodDefinition(
                m.Name,
                m.Parameters
                    .Select(p => CreateParameterDefinition(p, sourceName, sourceText))
                    .ToArray(),
                m.ReturnTypeName,
                m.DefaultBody,
                HasDefaultBody: m.DefaultBody is not null))
            .ToArray();

        var properties = trait.Properties
            .Select(p => new TraitPropertyDefinition(p.Name, p.TypeName, p.DefaultValue))
            .ToArray();

        var definition = new ToshTraitDefinition(
            trait.Name,
            methods,
            properties,
            sourceName,
            sourceText,
            trait.Span);

        definition.Documentation = trait.DocComment;
        DeclareType(trait.Name, definition, trait.Modifier, sourceName, sourceText, trait.Span);
        yield break;
    }

    /// <summary>
    /// Tries to invoke a user-defined binary operator method on <paramref name="instance"/>.
    /// The operator method receives <paramref name="other"/> as its argument, and optionally a
    /// second argument saying whether <paramref name="instance"/> was the *right* operand.
    /// </summary>
    /// <param name="reversed">
    /// <c>true</c> when this instance is the right operand of the expression, so the method is
    /// being asked for <c>other OP this</c> rather than <c>this OP other</c>.
    /// </param>
    /// <remarks>
    /// <c>TOAST-0106</c>. The single-argument form cannot express a non-commutative operator when
    /// the class is on the right: <c>10 - $p</c> and <c>$p - 10</c> arrive as the same call, so
    /// <c>ToastLib.Math.Point2D</c> answered <c>(-9, -8)</c> to both when the first should be
    /// <c>(9, 8)</c>. There was nothing the class could write instead — the information simply
    /// was not passed.
    ///
    /// The two-argument form is offered first and the one-argument form is the fallback, so every
    /// operator written before this keeps its exact behaviour and only a class that asks for the
    /// flag is told anything new.
    /// </remarks>
    private static async ValueTask<(bool Matched, object? Value)> TryInvokeClassBinaryOperatorAsync(
        ToshClassInstance instance,
        string @operator,
        object? other,
        bool reversed,
        CancellationToken cancellationToken)
    {
        var withOrientation = await instance.Definition.TryInvokeSpecialInstanceMethodAsync(
            instance,
            @operator,
            new object?[] { other, reversed },
            cancellationToken);
        if (withOrientation.Matched)
        {
            return withOrientation;
        }

        return await instance.Definition.TryInvokeSpecialInstanceMethodAsync(
            instance,
            @operator,
            new object?[] { other },
            cancellationToken);
    }

    /// <summary>
    /// Tries to invoke a zero-argument user-defined unary operator method on <paramref name="instance"/>.
    /// </summary>
    private static ValueTask<(bool Matched, object? Value)> TryInvokeClassUnaryOperatorAsync(
        ToshClassInstance instance,
        string @operator,
        CancellationToken cancellationToken)
    {
        return instance.Definition.TryInvokeSpecialInstanceMethodAsync(
            instance,
            @operator,
            Array.Empty<object?>(),
            cancellationToken);
    }

    /// <summary>
    /// Help text for an unknown variable, naming the shell namespace when the spelling is one
    /// people reach for out of habit from another shell.
    /// </summary>
    /// <remarks>
    /// Script arguments live at <c>$tosh.Script.Args</c>. Someone arriving from bash, Python or
    /// PowerShell writes <c>$args</c> or <c>$argv</c> first, got "declare it first with
    /// 'var args = ...'" — advice that points away from the answer — and had no path to the real
    /// spelling short of piping <c>$tosh.Script</c> through <c>members</c> (<c>TS-P2-44</c>).
    /// </remarks>
    /// <summary>
    /// Explains a primary-constructor parameter referenced where it no longer exists —
    /// <c>TS-P2-81</c>.
    /// </summary>
    /// <remarks>
    /// Measured, the parameter reaches a *stored* property initializer and a later parameter's
    /// default, and nothing else: a computed property, a getter block, a method body and a static
    /// initializer each fail, because each runs after construction while the parameter is a
    /// constructor local that was never stored. `prop X = $x` is the way to carry it forward.
    /// </remarks>
    private string? DescribeOutOfScopeConstructorParameter(string name)
    {
        if (CurrentClass is not { } cls) return null;

        foreach (var parameter in cls.PrimaryConstructorParameters)
        {
            if (!string.Equals(parameter.Name, name, StringComparison.Ordinal)) continue;

            return "a primary-constructor parameter is in scope in a stored property initializer "
                 + $"and in a later parameter's default. Declare 'prop {name} = ${name}' to reach "
                 + "it from the rest of the type.";
        }

        return null;
    }

    private bool TryGetClassDefinition(string name, out ToshClassDefinition definition)
    {
        if (TryGetNamedType(name, out var shellType) && shellType is ToshClassDefinition classDefinition)
        {
            definition = classDefinition;
            return true;
        }

        definition = null!;
        return false;
    }

    /// <summary>Marks the engine as running <paramref name="definition"/>'s own code.</summary>
    internal IDisposable EnterClass(ToshClassDefinition definition)
    {
        _executingClasses.Push(definition);
        return new ExecutingClassFrame(_executingClasses);
    }

    private sealed class ExecutingClassFrame(Stack<ToshClassDefinition> classes) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (classes.Count > 0)
            {
                classes.Pop();
            }
        }
    }

    /// <summary>
    /// Recognises the textual form <c>Bare&lt;arg1, arg2, …&gt;</c> as an
    /// instantiation of a user-defined generic <see cref="ToshClassDefinition"/>.
    /// Splits type-arguments at the top angle-bracket level, validates arity,
    /// and returns the matched class definition.
    /// </summary>
    private bool TryGetGenericClassAnnotation(
        string typeName,
        out ToshClassDefinition definition,
        out IReadOnlyList<string> typeArguments)
    {
        definition = null!;
        typeArguments = Array.Empty<string>();

        if (string.IsNullOrEmpty(typeName) || !typeName.EndsWith(">", StringComparison.Ordinal))
        {
            return false;
        }

        var lt = typeName.IndexOf('<');
        if (lt <= 0)
        {
            return false;
        }

        var bare = typeName[..lt];
        var inner = typeName.Substring(lt + 1, typeName.Length - lt - 2);

        if (!TryGetNamedType(bare, out var named) || named is not ToshClassDefinition cls || cls.TypeParameterNames.Count == 0)
        {
            return false;
        }

        var args = SplitTopLevelTypeArguments(inner);
        if (args.Count != cls.TypeParameterNames.Count)
        {
            return false;
        }

        definition = cls;
        typeArguments = args;
        return true;
    }

    /// <summary>
    /// Validates type-argument arity and constraints on a 'fulfills'
    /// clause referencing a generic interface. Type arguments that
    /// reference the implementing class's own type parameters are
    /// accepted without constraint checks (they're checked when the
    /// class is instantiated). Concrete types are validated against
    /// the interface's where-clauses.
    /// </summary>
    private void ValidateInterfaceTypeArguments(
        string sourceName,
        string sourceText,
        ClassDefinitionStatementSyntax @class,
        ToshInterfaceDefinition ifaceDefinition,
        string ifaceReference)
    {
        var lt = ifaceReference.IndexOf('<');
        var hasArgs = lt >= 0 && ifaceReference.EndsWith(">", StringComparison.Ordinal);
        var ifaceArity = ifaceDefinition.TypeParameterNames.Count;

        if (!hasArgs)
        {
            // Bare reference to an unparameterised or generic
            // interface. Generic interfaces require explicit type
            // arguments at fulfills sites.
            if (ifaceArity > 0)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.missing_interface_type_arguments",
                    Title: $"Class '{@class.Name}' fulfills generic interface '{ifaceDefinition.Name}' without type arguments.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: @class.Span,
                    Label: $"write 'fulfills {ifaceDefinition.Name}<{string.Join(", ", ifaceDefinition.TypeParameterNames)}>'"));
            }
            return;
        }

        var inner = ifaceReference.Substring(lt + 1, ifaceReference.Length - lt - 2);
        var args = SplitTopLevelTypeArguments(inner);

        if (ifaceArity == 0)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.unexpected_interface_type_arguments",
                Title: $"Interface '{ifaceDefinition.Name}' is not generic and does not accept type arguments.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: @class.Span,
                Label: $"remove '<{inner}>'"));
        }

        if (args.Count != ifaceArity)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.interface_type_argument_arity_mismatch",
                Title: $"Generic interface '{ifaceDefinition.Name}' expects {ifaceArity} type argument(s) <{string.Join(", ", ifaceDefinition.TypeParameterNames)}> but received {args.Count}.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: @class.Span,
                Label: $"<{string.Join(", ", args)}> has {args.Count} arg(s)"));
        }

        if (ifaceDefinition.TypeParameterConstraints.Count == 0)
        {
            return;
        }

        // Build map: interface type-param name → supplied argument string.
        var argByParam = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < ifaceArity; i++)
        {
            argByParam[ifaceDefinition.TypeParameterNames[i]] = args[i];
        }

        // Set of class's own type-parameter names — args matching one
        // are deferred (validated at instantiation).
        var classTypeParams = new HashSet<string>(
            @class.TypeParameters ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        foreach (var clause in ifaceDefinition.TypeParameterConstraints)
        {
            if (!argByParam.TryGetValue(clause.TypeParameter, out var argText))
            {
                continue;
            }
            argText = argText.Trim();
            if (classTypeParams.Contains(argText))
            {
                continue; // forwarded — defer to instantiation site
            }
            var bound = TryResolveTypeName(argText);
            if (bound is null)
            {
                continue; // unknown name — accept conservatively
            }

            foreach (var constraintName in clause.ConstraintNames)
            {
                bool satisfied;
                bool known;
                if (ToshTypeParameterConstraintRegistry.TryGet(constraintName, out var predicate))
                {
                    satisfied = predicate(bound);
                    known = true;
                }
                else
                {
                    var clr = TryResolveTypeName(constraintName);
                    if (clr is not null)
                    {
                        satisfied = clr.IsAssignableFrom(bound);
                        known = true;
                    }
                    else
                    {
                        satisfied = true;
                        known = false;
                    }
                }

                if (satisfied || !known) continue;

                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.interface_type_argument_constraint_violation",
                    Title: $"Generic interface '{ifaceDefinition.Name}' requires type parameter '{clause.TypeParameter}' to satisfy '{constraintName}', but '{argText}' (CLR {bound.FullName ?? bound.Name}) does not.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: @class.Span,
                    Label: $"'{argText}' does not satisfy '{constraintName}'"));
            }
        }
    }

    internal IReadOnlyList<object?> ExecuteClassBlockSync(
        ToshClassDefinition? declaringClass,
        string sourceName,
        string sourceText,
        BlockSyntax block,
        IReadOnlyDictionary<string, object?> locals,
        IReadOnlyList<LexicalScope>? capturedScopes,
        string callName)
    {
        using var executingClass = declaringClass is null ? null : EnterClass(declaringClass);
        using var executionFrame = ToshExecutionDepthGuard.Enter(
            LanguageRuntime.Options.MaxRecursionDepth,
            callName,
            sourceName,
            sourceText,
            block.Span);
        using var captured = PushCapturedScopes(capturedScopes);
        _functionCallStack.Push(callName);

        try
        {
            return AsyncEnumerableExtensions.ToListAsync(
                    ExecuteBlockAsync(
                        sourceName,
                        sourceText,
                        block,
                        CancellationToken.None,
                        locals))
                .GetAwaiter()
                .GetResult();
        }
        catch (ReturnSignalException signal)
        {
            return signal.Values;
        }
        finally
        {
            _functionCallStack.Pop();
        }
    }

    internal async Task<IReadOnlyList<object?>> ExecuteClassBlockAsync(
        ToshClassDefinition? declaringClass,
        string sourceName,
        string sourceText,
        BlockSyntax block,
        IReadOnlyDictionary<string, object?> locals,
        IReadOnlyList<LexicalScope>? capturedScopes,
        string callName,
        CancellationToken cancellationToken)
    {
        using var executingClass = declaringClass is null ? null : EnterClass(declaringClass);
        cancellationToken.ThrowIfCancellationRequested();
        using var executionFrame = ToshExecutionDepthGuard.Enter(
            LanguageRuntime.Options.MaxRecursionDepth,
            callName,
            sourceName,
            sourceText,
            block.Span);
        using var captured = PushCapturedScopes(capturedScopes);
        _functionCallStack.Push(callName);

        try
        {
            return await AsyncEnumerableExtensions.ToListAsync(
                ExecuteBlockAsync(
                    sourceName,
                    sourceText,
                    block,
                    cancellationToken,
                    locals),
                cancellationToken);
        }
        catch (ReturnSignalException signal)
        {
            return signal.Values;
        }
        finally
        {
            _functionCallStack.Pop();
        }
    }

    /// <summary>
    /// Wraps a class-body pipeline as a one-statement block, and projects the values it produced
    /// back into a single result — the two halves of <c>EvaluateClassPipelineValue</c> that do not
    /// depend on how the block is executed.
    /// </summary>
    /// <remarks>
    /// Extracted for <c>TS-P1-24</c>. The <c>Sync</c> and <c>Async</c> forms of this method were
    /// byte-identical apart from the block call, including the unwrapping switch below — and the
    /// twin inventory never counted them, because its discovery rule looked for
    /// <c>Foo</c>/<c>FooAsync</c> and these are named <c>FooSync</c>/<c>FooAsync</c>. The guard has
    /// been taught the second convention.
    /// </remarks>
    private static BlockSyntax BuildClassPipelineBlock(PipelineSyntax pipeline)
    {
        var span = pipeline.Stages.Count == 0
            ? default
            : TextSpan.FromBounds(pipeline.Stages[0].Span.Start, pipeline.Stages[^1].Span.End);

        return new BlockSyntax([new PipelineStatementSyntax(pipeline, span)], span);
    }

    /// <summary>
    /// Collapses a class-body pipeline's results, unwrapping <c>$this</c> self-references so a
    /// property or method returning <c>$this</c> yields the instance rather than the marker.
    /// </summary>
    private static object? ProjectClassPipelineValues(IReadOnlyList<object?> values)
    {
        return values.Count switch
        {
            0 => null,
            1 => values[0] is ToshClassSelfReference selfReference ? selfReference.Unwrap() : values[0],
            _ => values
                .Select(value => value is ToshClassSelfReference self ? self.Unwrap() : value)
                .ToArray(),
        };
    }

    internal object? EvaluateClassPipelineValueSync(
        ToshClassDefinition? declaringClass,
        string sourceName,
        string sourceText,
        PipelineSyntax pipeline,
        IReadOnlyDictionary<string, object?> locals,
        IReadOnlyList<LexicalScope>? capturedScopes,
        string callName = "<class>")
    {
        using var executingClass = declaringClass is null ? null : EnterClass(declaringClass);
        if (TryEvaluateShorthandLocalPipeline(pipeline, locals, out var shorthandValue))
        {
            return shorthandValue;
        }

        var values = ExecuteClassBlockSync(
            declaringClass,
            sourceName,
            sourceText,
            BuildClassPipelineBlock(pipeline),
            locals,
            capturedScopes,
            callName);

        return ProjectClassPipelineValues(values);
    }

    internal async ValueTask<object?> EvaluateClassPipelineValueAsync(
        ToshClassDefinition? declaringClass,
        string sourceName,
        string sourceText,
        PipelineSyntax pipeline,
        IReadOnlyDictionary<string, object?> locals,
        IReadOnlyList<LexicalScope>? capturedScopes,
        CancellationToken cancellationToken,
        string callName = "<class>")
    {
        using var executingClass = declaringClass is null ? null : EnterClass(declaringClass);
        if (TryEvaluateShorthandLocalPipeline(pipeline, locals, out var shorthandValue))
        {
            return shorthandValue;
        }

        var values = await ExecuteClassBlockAsync(
            declaringClass,
            sourceName,
            sourceText,
            BuildClassPipelineBlock(pipeline),
            locals,
            capturedScopes,
            callName,
            cancellationToken);

        return ProjectClassPipelineValues(values);
    }

    private static bool IsNumericEnumUnderlyingType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(byte) ||
               type == typeof(sbyte) ||
               type == typeof(short) ||
               type == typeof(ushort) ||
               type == typeof(int) ||
               type == typeof(uint) ||
               type == typeof(long) ||
               type == typeof(ulong);
    }

    /// <summary>
    /// The first way a class's implementation disagrees with the trait member it satisfies,
    /// or null when they agree — `TOAST-0020`.
    /// </summary>
    /// <remarks>
    /// <para>
    /// **Covariant returns, exact parameters.** A class may return the declared type or one
    /// derived from it, because narrowing a result never surprises a caller holding the
    /// trait. A parameter must match exactly: contravariance would be sound, but it is
    /// rarely wanted and frequently misread, and half a variance rule is worse than a
    /// simple one.
    /// </para>
    /// <para>
    /// An **undeclared** type on either side agrees with anything. A trait that says nothing
    /// about a return constrains nothing, and a class that says nothing has not contradicted
    /// the trait — it has only declined to repeat it.
    /// </para>
    /// </remarks>
    private ContractMemberTypeMismatch? ResolveMemberTypeMismatch(
        ToshClassDefinition definition,
        string memberName,
        IReadOnlyList<FunctionParameterDefinition> contractParameters,
        string? contractReturnTypeName)
    {
        var implementation = definition.Methods
            .FirstOrDefault(method => string.Equals(method.Name, memberName, StringComparison.OrdinalIgnoreCase));

        if (implementation is null)
        {
            // Absent, or inherited, or supplied by the trait's own default — all of which
            // the missing-member check above has already had its say about.
            return null;
        }

        return ContractMemberTypeRules.FindMethodMismatch(
            implementation.Parameters
                .Select(parameter => new ContractParameterType(parameter.Name, parameter.TypeName))
                .ToArray(),
            implementation.ReturnTypeName,
            contractParameters
                .Select(parameter => new ContractParameterType(parameter.Name, parameter.TypeName))
                .ToArray(),
            contractReturnTypeName,
            IsCovariantWith,
            NamesSameType);
    }

    /// <summary>
    /// Reports a contract-type disagreement, or does nothing when there is none.
    /// </summary>
    /// <remarks>
    /// Shared by traits and interfaces (`TOAST-0020`), which sit in neighbouring blocks and
    /// had the identical gap. One rule for both contract kinds means one thing to learn and
    /// one implementation to keep correct.
    /// </remarks>
    private void ThrowOnContractTypeMismatch(
        string sourceName,
        string sourceText,
        ClassDefinitionStatementSyntax @class,
        string contractName,
        string contractKind,
        string memberName,
        ContractMemberTypeMismatch? mismatch)
    {
        if (mismatch is not { } found)
        {
            return;
        }

        throw ToshDiagnosticException.Create(ContractMemberTypeRules.CreateDiagnostic(
            @class.Name,
            contractName,
            contractKind,
            memberName,
            found,
            sourceName,
            sourceText,
            @class.Span));
    }

    /// <summary>
    /// Whether a class's property disagrees with the type a trait declares for it.
    /// </summary>
    /// <remarks>
    /// **Invariant, decided 2026-08-17** — unlike a method's return. A property is written as
    /// well as read, so narrowing it is unsound: code holding the trait could assign the
    /// declared type into what the class narrowed, and the class's own annotation would try
    /// to coerce it and fail. The failure would land at the assignment, nowhere near the
    /// declaration that permitted it. C# and Java keep fields invariant for the same reason.
    /// </remarks>
    private ContractMemberTypeMismatch? ResolvePropertyTypeMismatch(
        ToshClassDefinition definition,
        TraitPropertyDefinition traitProperty)
    {
        var implementation = definition.Properties
            .FirstOrDefault(property => string.Equals(property.Name, traitProperty.Name, StringComparison.OrdinalIgnoreCase));

        if (implementation is null)
        {
            return null;
        }

        return ContractMemberTypeRules.FindPropertyMismatch(
            implementation.Name,
            implementation.TypeName,
            traitProperty.TypeName,
            NamesSameType);
    }

    /// <summary>Whether <paramref name="actual"/> is <paramref name="expected"/> or derives from it.</summary>
    private bool IsCovariantWith(string actual, string expected)
    {
        if (NamesSameType(actual, expected))
        {
            return true;
        }

        if (TryGetNamedType(StripGenericTypeArguments(actual), out var namedActual) &&
            namedActual is ToshClassDefinition actualClass)
        {
            // A class satisfies the expected name by inheriting it, implementing it as an
            // interface, or using it as a trait — the same three routes an annotation
            // accepts, asked through the walk that already answers them.
            if (actualClass.SatisfiesContract(expected))
            {
                return true;
            }

            for (var current = actualClass.BaseClass; current is not null; current = current.BaseClass)
            {
                if (NamesSameType(current.Name, expected))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Whether two annotations name the same type, so an alias and its CLR spelling agree.
    /// </summary>
    private bool NamesSameType(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var leftType = ResolveTypeName(StripGenericTypeArguments(left));
        var rightType = ResolveTypeName(StripGenericTypeArguments(right));

        return leftType is not null && rightType is not null && leftType == rightType;
    }
}
