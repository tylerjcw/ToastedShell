using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.RegularExpressions;
using Tosh.Runtime;
using Tosh.Language.Binding;
using Tosh.Language.Bridge;
using Tosh.Language.Debugging;
using Tosh.Language.Parsing;

namespace Tosh.Language;

public sealed partial class ToshEngine
{
    /// <summary>
    /// Methods added to types by <c>extend</c>, keyed by the type name written.
    /// </summary>
    /// <remarks>
    /// Registered when the declaration executes, which gives the visibility rule for
    /// free: a `require`d module runs its statements in this engine, so importing a
    /// library brings its extensions with it, the way a `using` brings C#'s
    /// (<c>TS-P3-27</c>).
    /// </remarks>
    private readonly Dictionary<string, Dictionary<string, FunctionDefinition>> _extensionMethods =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Statics added by <c>extend</c>, keyed the same way as <see cref="_extensionMethods"/>
    /// — <c>TOAST-0097</c>.
    /// </summary>
    /// <remarks>
    /// Kept separate because the two are reached from different places: an instance method
    /// needs a receiver, a static needs only the type. Before this, a <c>static func</c> in
    /// an <c>extend</c> block was filed in the instance table with its modifier discarded,
    /// so <c>Type::name()</c> found nothing while <c>$value.name()</c> found it — the
    /// declaration was not merely unreachable, it answered to the wrong call.
    /// </remarks>
    private readonly Dictionary<string, Dictionary<string, FunctionDefinition>> _extensionStatics =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The names an <c>extend</c> declaration may have used for this value.</summary>
    /// <summary>
    /// Union names this engine already knows, mapped to their variants, for the binder's
    /// exhaustiveness check — <c>TOAST-0083</c>.
    /// </summary>
    /// <remarks>
    /// The check was built from the source being bound, which is right for a union declared
    /// there and wrong for every other one: a `match` over the prelude's `Result` was neither
    /// judged exhaustive nor reported incomplete. Anything the engine holds — the core types,
    /// and whatever an import brought in — is offered here, and a declaration in the source
    /// still overrides it.
    /// </remarks>
    private IReadOnlyDictionary<string, IReadOnlyList<string>>? CollectAmbientUnionShapes()
    {
        Dictionary<string, IReadOnlyList<string>>? shapes = null;

        void Add(object? candidate)
        {
            if (candidate is not ToshUnionDefinition union || union.Variants.Count == 0)
            {
                return;
            }

            shapes ??= new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            shapes[union.Name] = union.Variants.Select(variant => variant.Name).ToArray();
        }

        foreach (var value in LanguageRuntime.Classes.Values)
        {
            Add(value);
        }

        foreach (var scope in _scopes)
        {
            foreach (var value in scope.Classes.Values)
            {
                Add(value);
            }
        }

        return shapes;
    }

    private static IEnumerable<string> EnumerateReceiverTypeNames(object receiver)
    {
        if (receiver is IShellTypedObject typed)
        {
            yield return typed.ShellTypeDescriptor.ShellTypeName;
            yield return typed.ShellTypeDescriptor.ShellFullName;
        }

        // `TOAST-0083`. A *bound* generic union names itself with its arguments — `Option<int>`
        // — while `extend Option { … }` registers under the bare name, so an extension on a
        // generic union could never be found. The declaration has no arguments to write and
        // `extend Option<T>` does not parse, so the bare name is the only thing an author can
        // key on and the receiver has to answer to it.
        if (receiver is ToshUnionVariantInstance unionVariant)
        {
            yield return unionVariant.UnionDefinition.Name;
        }

        var clr = receiver.GetType();
        yield return clr.Name;

        if (clr.FullName is { } full)
        {
            yield return full;
        }
    }

    private static IReadOnlyList<string> GetUnimplementedAbstractMethods(ToshClassDefinition parent, ToshClassDefinition child)
    {
        var missing = new List<string>();
        var current = parent;

        while (current is not null)
        {
            foreach (var method in current.Methods.Where(m => m.IsAbstract))
            {
                if (!child.Methods.Any(m => string.Equals(m.Name, method.Name, StringComparison.OrdinalIgnoreCase) && !m.IsAbstract))
                {
                    missing.Add(method.Name);
                }
            }
            current = current.BaseClass;
        }

        return missing;
    }

    private static IReadOnlyList<string> GetUnimplementedAbstractProperties(ToshClassDefinition parent, ToshClassDefinition child)
    {
        var missing = new List<string>();
        var current = parent;

        while (current is not null)
        {
            foreach (var prop in current.Properties.Where(p => p.IsAbstract))
            {
                if (!child.Properties.Any(p => string.Equals(p.Name, prop.Name, StringComparison.OrdinalIgnoreCase) && !p.IsAbstract))
                {
                    missing.Add(prop.Name);
                }
            }
            current = current.BaseClass;
        }

        return missing;
    }

    /// <summary>
    /// Whether any class in <paramref name="classDefinition"/>'s chain declares a method that
    /// <paramref name="method"/> would genuinely override: the same name *and* the same parameter
    /// list. Name alone is what <see cref="HasMethodInHierarchy"/> answers, which is right for
    /// asking whether `overrule` has anything at all to point at, and wrong for deciding whether
    /// a method shadows one — an overload shares the name by definition.
    /// </summary>
    private static bool OverridesMethodInHierarchy(
        ToshClassDefinition classDefinition,
        ToshClassMethodDefinition method)
    {
        for (var current = classDefinition; current is not null; current = current.BaseClass)
        {
            foreach (var candidate in current.Methods)
            {
                if (string.Equals(candidate.Name, method.Name, StringComparison.OrdinalIgnoreCase) &&
                    ParameterListsMatch(candidate.Parameters, method.Parameters))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasMethodInHierarchy(ToshClassDefinition classDefinition, string methodName)
    {
        var current = classDefinition;
        while (current is not null)
        {
            if (current.Methods.Any(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
            current = current.BaseClass;
        }
        return false;
    }

    private async IAsyncEnumerable<object?> EvaluateUnionDefinitionAsync(
        string sourceName,
        string sourceText,
        UnionDefinitionStatementSyntax union,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, union.Name, union.Span, "reserved runtime namespace");
        WarnIfShadowingCoreType(union.Name);
        WarnIfTypeParametersShadow(union.TypeParameters);

        var variants = union.Variants
            .Select(v => new UnionVariantDefinition(
                v.Name,
                v.Fields.Select(f => new UnionVariantFieldDefinition(
                    f.Name,
                    f.TypeName,
                    f.Span)).ToArray()))
            .ToArray();

        var definition = new ToshUnionDefinition(
            this,
            union.Name,
            variants,
            union.TypeParameters,
            sourceName,
            sourceText,
            union.Span);

        DeclareType(union.Name, definition, union.Modifier, sourceName, sourceText, union.Span);
        yield break;
    }

    private async IAsyncEnumerable<object?> EvaluateRecordDefinitionAsync(
        string sourceName,
        string sourceText,
        RecordDefinitionStatementSyntax record,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, record.Name, record.Span, "reserved runtime namespace");
        WarnIfShadowingCoreType(record.Name);
        WarnIfTypeParametersShadow(record.TypeParameters);

        var runtimeFields = record.Fields
            .Select(field => new ToshRecordFieldDefinition(
                field.Name,
                field.TypeName,
                field.DefaultValue,
                field.IsOptional,
                field.Span,
                CreateRefinementAnnotation(sourceName, sourceText, field.Refinement)))
            .ToArray();

        // Handle partial record merging
        if (record.IsPartial && TryGetNamedType(record.Name, out var existingType) && existingType is ToshRecordDefinition existingDef)
        {
            if (!existingDef.IsPartial)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.partial_merge_non_partial_record",
                    Title: $"Cannot merge partial record '{record.Name}' with existing non-partial record.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: record.Span,
                    Label: "original record is not declared 'partial'"));
            }

            existingDef.MergePartial(runtimeFields);
            DeclareType(record.Name, existingDef, record.Modifier, sourceName, sourceText, record.Span);
            yield break;
        }

        var duplicateFields = record.Fields
            .GroupBy(field => field.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicateFields is not null)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.duplicate_record_field",
                Title: $"Record '{record.Name}' defines field '{duplicateFields.Key}' more than once.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: duplicateFields.First().Span,
                Label: $"'{duplicateFields.Key}' is declared multiple times"));
        }

        var definition = new ToshRecordDefinition(
            this,
            record.Name,
            runtimeFields,
            sourceName,
            sourceText,
            record.Span,
            CaptureVisibleScopes(),
            typeParameterNames: record.TypeParameters,
            typeParameterConstraints: record.TypeParameterConstraints);
        definition.Documentation = record.DocComment;

        definition.IsSealed = record.IsSealed;
        definition.IsStrict = record.IsStrict;
        definition.IsPartial = record.IsPartial;
        definition.IsFluid = record.IsFluid;

        if (definition.IsStrict && definition.IsFluid)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.conflicting_modifiers",
                Title: $"Record '{record.Name}' cannot be both 'strict' and 'fluid'.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: record.Span,
                Label: "cannot combine 'strict' and 'fluid'"));
        }

        DeclareType(record.Name, definition, record.Modifier, sourceName, sourceText, record.Span);
        yield break;
    }

    /// <summary>
    /// Declares a <c>raw struct</c>: builds the shared layout plan, emits a real
    /// sequential-layout CLR type, and registers both the emitted type (for the
    /// interop type resolver) and an <see cref="IShellNamedType"/> façade (for
    /// `new`, `describe-type`, and `members`) from the one declaration.
    /// </summary>
    private async IAsyncEnumerable<object?> EvaluateRawStructDefinitionAsync(
        string sourceName,
        string sourceText,
        RawStructDefinitionStatementSyntax rawStruct,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, rawStruct.Name, rawStruct.Span, "reserved runtime namespace");
        WarnIfShadowingCoreType(rawStruct.Name);

        if (rawStruct.Fields.Count == 0)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.raw_struct_requires_fields",
                Title: $"Raw struct '{rawStruct.Name}' has no fields.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: rawStruct.Span,
                Label: "a native layout needs at least one field"));
        }

        var typeResolver = CreateScopedTypeResolver();
        var plan = Bridge.RawStructPlanBuilder.Build(
            rawStruct,
            name => typeResolver.Resolve(name),
            sourceName,
            sourceText);

        var clrType = Bridge.NativeStructTypeFactory.GetOrCreate(plan);

        // Field defaults are evaluated now, in declaration scope, and applied
        // when TōSh constructs a value — never by the marshaller.
        var defaults = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in rawStruct.Fields)
        {
            if (field.DefaultValue is null) continue;

            defaults[field.Name] = await EvaluatePipelineAsync(
                sourceName,
                sourceText,
                field.DefaultValue,
                cancellationToken).FirstOrDefaultAsync(cancellationToken);
        }

        var definition = new ToshRawStructDefinition(plan, clrType, defaults);

        DeclareType(rawStruct.Name, definition, rawStruct.Modifier, sourceName, sourceText, rawStruct.Span, clrType);
        yield break;
    }

    /// <summary>
    /// <c>raw callback Name(…) -&gt; ret</c> — emits the delegate type a native
    /// signature names when it takes a C function pointer, and registers it
    /// under <paramref name="rawCallback"/>'s name so
    /// <see cref="ResolveNativeInteropParameterType"/> finds it like any other
    /// native type.
    /// </summary>
    private IAsyncEnumerable<object?> EvaluateRawCallbackDefinitionAsync(
        string sourceName,
        string sourceText,
        RawCallbackDefinitionStatementSyntax rawCallback,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureBindingNameIsNotReserved(sourceName, sourceText, rawCallback.Name, rawCallback.Span, "reserved runtime namespace");

        var parameters = new List<NativeFunctionParameterDefinition>(rawCallback.Parameters.Count);

        foreach (var parameter in rawCallback.Parameters)
        {
            // A `buffer[n]` collapses into two ABI arguments and is decoded
            // after the call — an inbound convention with no meaning for a
            // callback, whose arguments arrive already formed.
            if (TryParseOutArrayParameter(parameter.TypeName, out _, out _))
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.native_callback_buffer_parameter",
                    Title: $"Callback '{rawCallback.Name}' cannot take a buffer parameter.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: parameter.Span,
                    Label: $"'{parameter.Name}' declares '{parameter.TypeName}'",
                    Help: "a callback receives whatever the caller passes; declare the pointer and length separately."));
            }

            // A ref/out callback parameter would need the value written back
            // into the caller's memory after the ToSh body ran. That write-back
            // has no design yet, and a silently-ignored `out` is worse than a
            // rejected one.
            if (parameter.PassingMode != NativeParameterPassingMode.In)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.native_callback_by_reference_parameter",
                    Title: $"Callback '{rawCallback.Name}' cannot take a by-reference parameter.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: parameter.Span,
                    Label: $"'{parameter.Name}' is declared {parameter.PassingMode.ToString().ToLowerInvariant()}",
                    Help: "declare it as a pointer and use native-read / native-write inside the callback."));
            }

            parameters.Add(new NativeFunctionParameterDefinition(
                parameter.Name,
                parameter.TypeName ?? string.Empty,
                ResolveNativeInteropParameterType(
                    parameter.TypeName, parameter.PassingMode, sourceName, sourceText, parameter.Span,
                    $"callback parameter '{parameter.Name}'"),
                parameter.PassingMode));
        }

        var returnType = ResolveNativeInteropReturnType(rawCallback.ReturnTypeName, sourceName, sourceText, rawCallback.Span);

        // `ok` / `count` decide whether a native call *failed*. A callback's
        // return value is one we produce, so there is nothing to check and the
        // convention would silently do nothing.
        if (returnType.Convention != NativeErrorConvention.None)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.native_callback_return_convention",
                Title: $"Callback '{rawCallback.Name}' cannot declare a success convention.",
                SourceName: sourceName,
                SourceText: sourceText,
                Span: rawCallback.Span,
                Label: $"'{rawCallback.ReturnTypeName}' checks a result rather than producing one",
                Help: "write the concrete return type, such as '-> int'."));
        }

        var callingConvention = ResolveNativeCallingConvention(
            rawCallback.CallingConventionName, sourceName, sourceText, rawCallback.Span);

        var clrType = Bridge.NativeDelegateTypeFactory.GetOrCreate(parameters, returnType.ClrType, callingConvention);

        var definition = new ToshNativeCallbackDefinition(
            rawCallback.Name, clrType, parameters, returnType, callingConvention);

        DeclareType(rawCallback.Name, definition, rawCallback.Modifier, sourceName, sourceText, rawCallback.Span, clrType);
        return AsyncEnumerableExtensions.Empty<object?>();
    }

    private async IAsyncEnumerable<object?> EvaluateStructDefinitionAsync(
        string sourceName,
        string sourceText,
        StructDefinitionStatementSyntax @struct,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, @struct.Name, @struct.Span, "reserved runtime namespace");
        WarnIfShadowingCoreType(@struct.Name);

        var runtimeFields = @struct.Fields
            .Select(field => new ToshRecordFieldDefinition(
                field.Name,
                field.TypeName,
                field.DefaultValue,
                field.IsOptional,
                field.Span,
                CreateRefinementAnnotation(sourceName, sourceText, field.Refinement)))
            .ToArray();

        // Handle partial struct merging
        if (@struct.IsPartial && TryGetNamedType(@struct.Name, out var existingType) && existingType is ToshStructDefinition existingDef)
        {
            if (!existingDef.IsPartial)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.partial_merge_non_partial_struct",
                    Title: $"Cannot merge partial struct '{@struct.Name}' with existing non-partial struct.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: @struct.Span,
                    Label: "original struct is not declared 'partial'"));
            }

            existingDef.MergePartial(runtimeFields);
            DeclareType(@struct.Name, existingDef, @struct.Modifier, sourceName, sourceText, @struct.Span);
            yield break;
        }

        // Build properties, methods and constructors from body members
        var properties = new List<ToshClassPropertyDefinition>();
        var methods = new List<ToshClassMethodDefinition>();
        var constructors = new List<ToshClassConstructorDefinition>();

        foreach (var member in @struct.Members)
        {
            switch (member)
            {
                // `TS-P2-83`. This switch had no constructor case, so a declared struct
                // constructor was parsed and then silently dropped.
                case ClassConstructorMemberSyntax ctor:
                    constructors.Add(new ToshClassConstructorDefinition(
                        ctor.Parameters
                            .Select(p => CreateParameterDefinition(p, sourceName, sourceText))
                            .ToArray(),
                        ctor.Body,
                        sourceName,
                        sourceText,
                        ctor.Span,
                        CaptureVisibleScopes()));
                    break;
                case ClassPropertyMemberSyntax property:
                    properties.Add(new ToshClassPropertyDefinition(
                        property.Name,
                        property.TypeName,
                        property.Initializer,
                        property.GetterBody,
                        property.SetterBody,
                        property.IsShy,
                        property.IsStatic,
                        property.IsFixed || !@struct.IsFluid,
                        property.IsVital,
                        property.IsGuarded,
                        property.IsLazy,
                        property.IsFading,
                        property.IsLocal,
                        property.IsAbstract,
                        property.Span,
                        CreateRefinementAnnotation(sourceName, sourceText, property.Refinement)));
                    break;
                case ClassMethodMemberSyntax method:
                    methods.Add(new ToshClassMethodDefinition(
                        method.Method.Name,
                        method.Method.Parameters
                            .Select(p => CreateParameterDefinition(p, sourceName, sourceText))
                            .ToArray(),
                        method.Method.ReturnTypeName,
                        method.Method.Body,
                        method.IsStatic,
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
                        CapturedScopes: CaptureVisibleScopes()));
                    break;
            }
        }

        var definition = new ToshStructDefinition(
            this,
            @struct.Name,
            runtimeFields,
            properties,
            methods,
            sourceName,
            sourceText,
            @struct.Span,
            CaptureVisibleScopes(),
            constructors);

        definition.Documentation = @struct.DocComment;
        definition.IsSealed = @struct.IsSealed;
        definition.IsFluid = @struct.IsFluid;
        definition.IsPartial = @struct.IsPartial;

        DeclareType(@struct.Name, definition, @struct.Modifier, sourceName, sourceText, @struct.Span);
        yield break;
    }

    internal IAsyncEnumerable<object?> InvokeStructStaticMethodAsync(
        ToshStructDefinition structDef,
        ToshClassMethodDefinition method,
        IReadOnlyList<object?> arguments)
    {
        var locals = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < method.Parameters.Count && i < arguments.Count; i++)
        {
            locals[method.Parameters[i].Name] = arguments[i];
        }
        locals["args"] = arguments.ToArray();

        var values = ExecuteClassBlockSync(
            null,
            method.SourceName,
            method.SourceText,
            method.Body,
            locals,
            method.CapturedScopes,
            $"{structDef.Name}.{method.Name}");

        return values.ToAsyncEnumerable();
    }

    private async IAsyncEnumerable<object?> EvaluateEventDefinitionAsync(
        string sourceName,
        string sourceText,
        EventDefinitionStatementSyntax @event,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureBindingNameIsNotReserved(sourceName, sourceText, @event.Name, @event.Span, "reserved runtime namespace");

        var definition = new ToshEventDefinition(
            this,
            @event.Name,
            @event.Fields
                .Select(field => new ToshEventFieldDefinition(field.Name, field.TypeName, field.DefaultValue, field.Span))
                .ToArray(),
            @event.IsRequired,
            @event.IsLocal,
            sourceName,
            sourceText,
            @event.Span,
            CaptureVisibleScopes());

        if (definition.IsRequired)
        {
            LanguageRuntime.Events.MarkRequired(definition.Name);
        }

        if (definition.IsLocal && _scopes.Count > 0)
        {
            _scopes.Peek().LocalEventNames.Add(definition.Name);
        }

        DeclareVariable(definition.Name, new VariableBinding(definition, ReplayAsPipeline: false, IsAllocatedOnly: false), @event.Modifier);
        yield break;
    }

    private async Task<object?> ResolveMethodCallTargetAsync(
        string sourceName,
        string sourceText,
        MethodCallArgumentSyntax methodCall,
        CancellationToken cancellationToken)
    {
        if (!ShouldAutoMaterializeListTarget(methodCall.MethodName) ||
            !TryDecomposeMemberAssignmentTarget(methodCall.Target, out var rootExpression, out var memberPath))
        {
            return await EvaluateArgumentAsync(sourceName, sourceText, methodCall.Target, cancellationToken);
        }

        var rootTarget = await EvaluateOrMaterializeRootTargetAsync(sourceName, sourceText, rootExpression, cancellationToken);

        try
        {
            var existingTarget = await LanguageRuntime.ObjectAccessor.GetValueAsync(
                rootTarget,
                memberPath,
                cancellationToken);

            if (existingTarget is not null)
            {
                return existingTarget;
            }
        }
        catch (Exception exception) when (
            exception is not ToshDiagnosticException and not OperationCanceledException)
        {
        }

        var materializedList = new List<object?>();

        try
        {
            await LanguageRuntime.ObjectAccessor.SetValueAsync(
                rootTarget,
                memberPath,
                materializedList,
                cancellationToken);
            return materializedList;
        }
        catch (Exception exception) when (
            exception is not ToshDiagnosticException and not OperationCanceledException)
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.list_materialization_failed",
                Title: exception.Message,
                SourceName: sourceName,
                SourceText: sourceText,
                Span: methodCall.Target.Span,
                Label: $"while preparing '{memberPath}' for '{methodCall.MethodName}'"));
        }
    }

    private async Task<object?> EvaluateOrMaterializeRootTargetAsync(
        string sourceName,
        string sourceText,
        ArgumentSyntax rootExpression,
        CancellationToken cancellationToken)
    {
        if (rootExpression is VariableReferenceArgumentSyntax variableReference &&
            TryGetVariableBinding(variableReference.Name, out var existingBinding) &&
            existingBinding.IsAllocatedOnly)
        {
            var target = new System.Dynamic.ExpandoObject();

            TryAssignVariable(
                variableReference.Name,
                existingBinding with
                {
                    Value = target,
                    ReplayAsPipeline = false,
                    IsAllocatedOnly = false,
                });

            return target;
        }

        return await EvaluateArgumentAsync(sourceName, sourceText, rootExpression, cancellationToken);
    }

    /// <summary>
    /// Phase 6.16 — call-site type-argument inference for generic
    /// classes. When the user writes <c>new Box(42)</c> without the
    /// <c>&lt;int&gt;</c> ceremony, walk each generic type parameter
    /// and look for the *first* primary-constructor parameter whose
    /// raw annotation is exactly that type-parameter name (e.g.
    /// <c>x: T</c>). Use the runtime CLR type of the corresponding
    /// constructor argument as the inferred binding for that
    /// type-parameter. All type-parameters must be inferred or the
    /// helper returns false. Limited to bare type-parameter
    /// annotations — nested shapes like <c>list&lt;T&gt;</c> are
    /// out of scope for this phase.
    /// </summary>
    private static bool TryInferTypeArgumentsFromCtorArgs(
        IReadOnlyList<string> typeParameterNames,
        IReadOnlyList<FunctionParameterDefinition> ctorParameters,
        IReadOnlyList<object?> ctorArguments,
        out Type?[] resolved,
        out string[] display)
    {
        var bindings = new Dictionary<string, Type>(StringComparer.Ordinal);
        var paramSet = new HashSet<string>(typeParameterNames, StringComparer.Ordinal);

        for (int i = 0; i < ctorParameters.Count && i < ctorArguments.Count; i++)
        {
            var raw = ctorParameters[i].RawTypeName;
            if (raw is null) continue;
            UnifyCtorAnnotationWithValue(paramSet, raw, ctorArguments[i], bindings);
        }

        return FinishCtorInference(typeParameterNames, bindings, out resolved, out display);
    }

    /// <summary>Records mirror class inference but use field annotations
    /// as the primary-constructor surface.</summary>
    private static bool TryInferTypeArgumentsFromRecordFields(
        IReadOnlyList<string> typeParameterNames,
        IReadOnlyList<ToshRecordFieldDefinition> fields,
        IReadOnlyList<object?> ctorArguments,
        out Type?[] resolved,
        out string[] display)
    {
        var bindings = new Dictionary<string, Type>(StringComparer.Ordinal);
        var paramSet = new HashSet<string>(typeParameterNames, StringComparer.Ordinal);

        for (int i = 0; i < fields.Count && i < ctorArguments.Count; i++)
        {
            var raw = fields[i].TypeName;
            if (raw is null) continue;
            UnifyCtorAnnotationWithValue(paramSet, raw, ctorArguments[i], bindings);
        }

        return FinishCtorInference(typeParameterNames, bindings, out resolved, out display);
    }

    private static bool FinishCtorInference(
        IReadOnlyList<string> typeParameterNames,
        Dictionary<string, Type> bindings,
        out Type?[] resolved,
        out string[] display)
    {
        resolved = new Type?[typeParameterNames.Count];
        display = new string[typeParameterNames.Count];
        for (int p = 0; p < typeParameterNames.Count; p++)
        {
            if (!bindings.TryGetValue(typeParameterNames[p], out var bound))
            {
                resolved = Array.Empty<Type?>();
                display = Array.Empty<string>();
                return false;
            }
            resolved[p] = bound;
            display[p] = bound.Name;
        }
        return true;
    }

    /// <summary>
    /// Resolves a dotted-path access like <c>Lib.greeting</c> or
    /// <c>App.Math.add</c> against modules, classes, enums, and CLR
    /// types in scope. Returns the path string itself if no match is
    /// found.
    /// </summary>
    private object? ResolveQualifiedAccessOrFallback(string path)
    {
        if (TryResolveQualifiedAccess(path, out var value, out _))
        {
            return value;
        }

        return path;
    }

    private bool TryInvokeGenericUnionVariant(
        string path,
        IReadOnlyList<object?> arguments,
        IReadOnlyList<string> typeArgumentNames,
        out object? value)
    {
        var lastDot = path.LastIndexOf('.');
        if (lastDot <= 0 || lastDot == path.Length - 1 ||
            !TryGetNamedType(path[..lastDot], out var named) ||
            named is not ToshUnionDefinition union)
        {
            value = null;
            return false;
        }

        var invocation = union.InvokeGenericVariant(path[(lastDot + 1)..], arguments, typeArgumentNames);
        value = invocation.ReturnedVoid ? null : invocation.Value;
        return true;
    }

    /// <summary>What a resolved dotted path turns out to name.</summary>
    private enum QualifiedInvocationKind
    {
        /// <summary>A static method on the resolved type.</summary>
        Static,

        /// <summary>An instance method, reached by walking members from the resolved type.</summary>
        Instance,
    }

    /// <summary>
    /// Where a dotted invocation path lands: the type it resolves against, the member chain to
    /// walk from there, and the method to call at the end.
    /// </summary>
    private readonly record struct QualifiedInvocationPlan(
        QualifiedInvocationKind Kind,
        Type DeclaringType,
        string MethodName,
        IReadOnlyList<string> MemberPath);

    /// <summary>
    /// Rejects a path that names a type rather than a method, with the message that says what to
    /// write instead.
    /// </summary>
    private static InvalidOperationException ConstructInsteadOfInvoking(string path) =>
        new($"Construct instances with 'new {path}(...)'.");

    /// <summary>
    /// Works out what <paramref name="path"/> names, without invoking anything.
    /// </summary>
    /// <remarks>
    /// This is what the two <c>InvokeQualifiedMethod</c> twins duplicated: rejecting a path that
    /// is really a type, splitting the dotted path, and the longest-prefix scan that decides
    /// whether the call is static or instance and how much of the tail is a member chain. Both
    /// then did the same thing with the answer, differing only in whether the invocation was
    /// awaited (<c>TS-P1-24</c>).
    /// </remarks>
    /// <summary>
    /// Plans a static access whose head is a CLR type spelled differently from the shell alias it
    /// collides with — <c>TS-P2-37</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>File.ReadAllText(...)</c> reported "No overload matched static method 'ReadAllText' on
    /// 'System.IO.FileInfo'", naming a type the reader never wrote: the alias table is matched
    /// case-insensitively, so <c>File</c> found <c>file</c>. <c>Array.IndexOf</c> and
    /// <c>Tuple.Create</c> failed the same way against the shell's own <c>array</c> and
    /// <c>tuple</c> static types, which are reached through a different lookup — so the check
    /// runs ahead of both, at the one point they share.
    /// </para>
    /// <para>
    /// Only the *head* of a dotted path is treated this way, which is where a type is used as a
    /// type. Annotations resolve elsewhere and are untouched, so <c>var f: file</c> still binds
    /// <c>FileInfo</c>, as does <c>var f: File</c>.
    /// </para>
    /// </remarks>
    private bool TryPlanAliasCaseVariantAccess(string path, out Type type, out string[] members)
    {
        type = null!;
        members = System.Array.Empty<string>();

        var segments = SplitQualifiedPath(path);
        if (segments.Length < 2) return false;

        if (CreateScopedTypeResolver().ResolveAliasCaseVariant(segments[0]) is not { } resolved)
        {
            return false;
        }

        type = resolved;
        members = segments[1..];
        return true;
    }

    /// <summary>Whether a qualified path already resolves, without raising if it does not.</summary>
    /// <remarks>
    /// Asked before autoloading so the library is consulted only for a name nothing else
    /// can answer. <see cref="PlanQualifiedInvocation"/> reports by throwing, which is the
    /// right shape for the caller that has run out of options and the wrong one for a
    /// question.
    /// </remarks>
    private bool CanPlanQualifiedInvocation(string path)
    {
        try
        {
            PlanQualifiedInvocation(path);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (ToshDiagnosticException)
        {
            // `ConstructInsteadOfInvoking` and friends: the path resolved to something, and
            // what to do about it is not this question.
            return true;
        }
    }

    private QualifiedInvocationPlan PlanQualifiedInvocation(string path)
    {
        if (ResolveTypeName(path) is not null)
        {
            throw ConstructInsteadOfInvoking(path);
        }

        var segments = SplitQualifiedPath(path);

        // Longest prefix first: `A.B.C.method` prefers a type `A.B.C` over a type `A.B` with a
        // member `C`, which is what makes nested types resolve before member chains.
        for (var prefixLength = segments.Length - 1; prefixLength >= 1; prefixLength--)
        {
            var type = ResolveTypeName(string.Join('.', segments.Take(prefixLength)));

            if (type is null)
            {
                continue;
            }

            return prefixLength == segments.Length - 1
                ? new QualifiedInvocationPlan(
                    QualifiedInvocationKind.Static,
                    type,
                    segments[^1],
                    Array.Empty<string>())
                : new QualifiedInvocationPlan(
                    QualifiedInvocationKind.Instance,
                    type,
                    segments[^1],
                    segments[prefixLength..^1]);
        }

        throw new InvalidOperationException($"Unable to resolve .NET access path '{path}'.");
    }

    /// <summary>
    /// Resolves call-site type-argument names to CLR types — <c>TS-P2-82</c>.
    /// </summary>
    private IReadOnlyList<Type> ResolveExplicitTypeArguments(
        IReadOnlyList<string> names,
        string sourceName,
        string sourceText,
        TextSpan span)
    {
        var resolved = new List<Type>(names.Count);

        foreach (var name in names)
        {
            if (ResolveTypeName(name) is not { } type)
            {
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.unknown_type",
                    Title: $"Type '{name}' was not found.",
                    SourceName: sourceName,
                    SourceText: sourceText,
                    Span: span,
                    Label: $"'{name}' is not a type this session can resolve",
                    Help: "use a fully qualified name, or add a 'using' for its namespace."));
            }

            resolved.Add(type);
        }

        return resolved;
    }

    /// <summary>
    /// Invokes a callable that was found in a property rather than declared as a
    /// method (`TS-P2-93`).
    /// </summary>
    /// <remarks>
    /// Returns an <see cref="InvocationResult"/> because the class dispatch paths
    /// it serves are method-invocation paths: from the caller's side
    /// <c>$obj.Fn(9)</c> is a call, and which side of the class the callable was
    /// stored on is not something the call site should have to know.
    /// </remarks>
    internal async ValueTask<InvocationResult> InvokeHeldCallableAsync(
        IShellCallable callable,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken)
    {
        var context = new CommandContext(
            LanguageRuntime,
            AsyncEnumerableExtensions.Empty<object?>(),
            arguments,
            cancellationToken,
            Invocation: null,
            IsPipelined: false,
            ScopedTypeResolver: CreateScopedTypeResolver(),
            BlockExecutor: _ownBlockExecutor,
            ScopedCommands: CreateScopedCommandView(),
            ShellTypes: this);

        var results = await AsyncEnumerableExtensions.ToListAsync(
            callable.InvokeAsync(context),
            cancellationToken);

        return results.Count switch
        {
            0 => new InvocationResult(null, ReturnedVoid: true),
            1 => new InvocationResult(results[0], ReturnedVoid: false),
            _ => new InvocationResult(results, ReturnedVoid: false),
        };
    }

    /// <summary>
    /// Runs a callable to completion on the calling thread, for a CLR delegate wrapping one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Blocking is correct here rather than merely convenient. The caller is a .NET type
    /// raising an event — a widget reporting a keystroke — and it is mid-operation: it has
    /// nowhere to await and nothing useful to do until the handler has run. The TUI's pull
    /// bindings already call script functions from inside the render loop this way.
    /// </para>
    /// <para>
    /// The engine is single-threaded, and this keeps it so: the handler runs on whichever
    /// thread raised the event, which is the thread the engine is already on whenever the
    /// event came from something the script itself set running.
    /// </para>
    /// </remarks>
    internal object? InvokeCallableOnThisThread(IShellCallable callable, IReadOnlyList<object?> arguments)
    {
        var result = InvokeHeldCallableAsync(callable, arguments, CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();

        return result.ReturnedVoid ? null : result.Value;
    }

    /// <summary>
    /// Invokes a callable in expression position, where exactly one value is
    /// expected.
    /// </summary>
    /// <remarks>
    /// One helper for the two parses that reach it. `f() + 1` builds a
    /// <c>CallableInvocationArgumentSyntax</c>; the same text inside an
    /// interpolation hole is re-parsed as a pure expression and builds a
    /// <c>StaticMethodCallArgumentSyntax</c> instead. They are the same call and
    /// must behave the same way, including the "produced N values" diagnostic —
    /// duplicating that here is the `TS-P1-24` shape.
    /// </remarks>
    private async ValueTask<object?> InvokeCallableInExpressionAsync(
        IShellCallable callable,
        IReadOnlyList<object?> arguments,
        string sourceName,
        string sourceText,
        TextSpan span,
        IReadOnlyList<TextSpan> argumentSpans,
        CancellationToken cancellationToken)
    {
        var invocation = new CommandInvocation(
            sourceName,
            sourceText,
            callable.CallableName,
            span,
            argumentSpans);

        var context = new CommandContext(
            LanguageRuntime,
            AsyncEnumerableExtensions.Empty<object?>(),
            arguments,
            cancellationToken,
            invocation,
            IsPipelined: false,
            ScopedTypeResolver: CreateScopedTypeResolver(),
            BlockExecutor: _ownBlockExecutor,
            ScopedCommands: CreateScopedCommandView(),
            ShellTypes: this);

        var results = await AsyncEnumerableExtensions.ToListAsync(
            callable.InvokeAsync(context),
            cancellationToken);

        if (results.Count <= 1)
        {
            return results.Count == 1 ? results[0] : null;
        }

        throw ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.runtime.callable_invocation_requires_single_value",
            Title: "Callable invocation in expression context must produce exactly one value.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: span,
            Label: results.Count == 0
                ? "this invocation produced no values"
                : $"this invocation produced {results.Count} values",
            Help: "ensure the callable returns exactly one value, or use 'invoke' in pipeline context for multi-value output."));
    }

    private async ValueTask<object?> InvokeQualifiedMethodAsync(
        string path,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken,
        IReadOnlyList<Type>? typeArguments = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (TryPlanAliasCaseVariantAccess(path, out var caseVariantType, out var caseVariantMembers) &&
            caseVariantMembers.Length == 1)
        {
            var caseVariantCall = await LanguageRuntime.Invoker.InvokeStaticMethodAsync(
                caseVariantType,
                caseVariantMembers[0],
                arguments,
                typeArguments,
                cancellationToken);
            return caseVariantCall.ReturnedVoid ? null : caseVariantCall.Value;
        }

        if (TryResolveShellStaticType(path, out _))
        {
            throw ConstructInsteadOfInvoking(path);
        }

        var shellInvocation = await TryInvokeShellSymbolAsync(path, arguments, cancellationToken, typeArguments);

        if (shellInvocation.Matched)
        {
            return shellInvocation.Value;
        }

        // Nothing here knows the name yet. Before giving up, ask whether the reader's
        // library exports it under the module they named — `ToastLib.Math.Clamp(…)` says
        // which module it wants, so loading it is a lookup rather than a guess. Only tried
        // once the ordinary paths have all declined, so it can never shadow something that
        // already resolves.
        if (!CanPlanQualifiedInvocation(path) &&
            await TryAutoloadQualifiedNameAsync(path, cancellationToken))
        {
            var afterLoad = await TryInvokeShellSymbolAsync(path, arguments, cancellationToken, typeArguments);

            if (afterLoad.Matched)
            {
                return afterLoad.Value;
            }
        }

        var plan = PlanQualifiedInvocation(path);

        if (plan.Kind == QualifiedInvocationKind.Static)
        {
            var invocation = await LanguageRuntime.Invoker.InvokeStaticMethodAsync(
                plan.DeclaringType,
                plan.MethodName,
                arguments,
                typeArguments,
                cancellationToken);
            return invocation.ReturnedVoid ? null : invocation.Value;
        }

        var target = await ResolveQualifiedMemberChainAsync(
            plan.DeclaringType,
            plan.MemberPath,
            cancellationToken);

        if (target is null)
        {
            throw new InvalidOperationException("Cannot invoke an instance method on null.");
        }

        var instanceInvocation = await LanguageRuntime.Invoker.InvokeInstanceMethodAsync(
            target,
            plan.MethodName,
            arguments,
            cancellationToken);
        return instanceInvocation.ReturnedVoid ? target : instanceInvocation.Value;
    }

    private bool TryResolveQualifiedAccess(string path, out object? value, out bool matchedType)
    {
        if (TryPlanAliasCaseVariantAccess(path, out var caseVariantType, out var caseVariantMembers))
        {
            matchedType = true;
            value = ResolveQualifiedMemberChain(caseVariantType, caseVariantMembers);
            return true;
        }

        if (TryResolveShellSymbolAccess(path, out value))
        {
            matchedType = true;
            return true;
        }

        var directType = ResolveTypeName(path);

        if (directType is not null)
        {
            matchedType = true;
            value = directType;
            return true;
        }

        var segments = SplitQualifiedPath(path);
        matchedType = false;

        for (var prefixLength = segments.Length - 1; prefixLength >= 1; prefixLength--)
        {
            var type = ResolveTypeName(string.Join('.', segments.Take(prefixLength)));

            if (type is null)
            {
                continue;
            }

            matchedType = true;
            value = ResolveQualifiedMemberChain(type, segments[prefixLength..]);
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>Which kind of shell symbol a dotted path names, if any.</summary>
    private enum ShellSymbolKind
    {
        /// <summary>Not a shell symbol; the caller should try CLR resolution.</summary>
        None,

        /// <summary>A member reached from a module, possibly through a nested member path.</summary>
        Module,

        /// <summary>A static member of a shell type.</summary>
        ShellStatic,
    }

    /// <summary>
    /// A dotted path resolved against the shell's own symbols, before any invocation happens.
    /// </summary>
    /// <param name="MemberPath">
    /// The dotted path from the module to the object holding the method, or <see langword="null"/>
    /// when the method is on the module itself.
    /// </param>
    private readonly record struct ShellSymbolPlan(
        ShellSymbolKind Kind,
        object? Module,
        IShellStaticType? StaticType,
        string MethodName,
        string? MemberPath);

    private bool TryPlanShellSymbol(string path, out ShellSymbolPlan plan)
    {
        var segments = SplitQualifiedPath(path);

        // `TS-P2-100`. A module claims a dotted path only when it actually exports
        // the next segment. Claiming it unconditionally made a module named after a
        // CLR namespace swallow that namespace for the whole session: a profile
        // declaring `module System { … }` turned `System.Convert.ToInt32(…)` in an
        // unrelated file into "Member 'Convert' was not found on type
        // 'ToshModuleObject'".
        //
        // This is the rule the specification already states for a module named
        // after a CLR *type* — its own exports win, and a name it does not export
        // is looked up on the shadowed type — applied to namespaces, which had no
        // fall-through at all.
        if (segments.Length >= 2 &&
            TryGetModule(segments[0], out var module) &&
            ModuleClaimsPath(module, segments[0], segments[1]))
        {
            plan = new ShellSymbolPlan(
                ShellSymbolKind.Module,
                module,
                StaticType: null,
                segments[^1],
                segments.Length > 2 ? string.Join('.', segments[1..^1]) : null);
            return true;
        }

        // `TS-P2-92`. This required *exactly* two segments, so `C.Method(...)`
        // resolved and `C.Prop.Method(...)` did not — a static property's value
        // could be read (`C.Text.Length` works) but never called. A module has
        // carried a member chain here all along; a shell static type now does too,
        // and the two invokers walk it the same way.
        if (segments.Length >= 2 && TryResolveShellStaticType(segments[0], out var shellType))
        {
            plan = new ShellSymbolPlan(
                ShellSymbolKind.ShellStatic,
                Module: null,
                shellType,
                segments[^1],
                segments.Length > 2 ? string.Join('.', segments[1..^1]) : null);
            return true;
        }

        plan = default;
        return false;
    }

    private static InvalidOperationException CannotInvokeOnNull(string methodName) =>
        new($"Cannot invoke '{methodName}' on null.");

    private async ValueTask<(bool Matched, object? Value)> TryInvokeShellSymbolAsync(
        string path,
        IReadOnlyList<object?> arguments,
        CancellationToken cancellationToken,
        IReadOnlyList<Type>? typeArguments = null)
    {
        if (!TryPlanShellSymbol(path, out var plan))
        {
            return (false, null);
        }

        if (plan.Kind == ShellSymbolKind.ShellStatic)
        {
            if (plan.MemberPath is null)
            {
                var staticInvocation = await LanguageRuntime.Invoker.InvokeStaticMethodAsync(
                    plan.StaticType!,
                    plan.MethodName,
                    arguments,
                    cancellationToken,
                    typeArguments);
                return (true, staticInvocation.ReturnedVoid ? null : staticInvocation.Value);
            }

            var staticTarget = await LanguageRuntime.ObjectAccessor.GetValueAsync(
                                   plan.StaticType,
                                   plan.MemberPath,
                                   cancellationToken)
                               ?? throw CannotInvokeOnNull(plan.MethodName);
            var chained = await InvokeResolvedTargetAsync(
                staticTarget, plan.MethodName, arguments, typeArguments, cancellationToken);
            return (true, chained.ReturnedVoid ? staticTarget : chained.Value);
        }

        var target = plan.Module!;

        if (plan.MemberPath is not null)
        {
            target = await LanguageRuntime.ObjectAccessor.GetValueAsync(
                         plan.Module,
                         plan.MemberPath,
                         cancellationToken)
                     ?? throw CannotInvokeOnNull(plan.MethodName);
        }

        var invocation = await InvokeResolvedTargetAsync(
            target, plan.MethodName, arguments, typeArguments, cancellationToken);
        return (true, invocation.ReturnedVoid ? target : invocation.Value);
    }

    /// <summary>
    /// Invokes a method on a target reached through a module or member path, carrying any
    /// call-site type arguments — <c>TOAST-0118</c>.
    /// </summary>
    /// <remarks>
    /// A class reached this way — <c>M.A.NoArg&lt;int&gt;()</c> — arrives as the class
    /// *definition*, so the call is a static one even though the path looks like member
    /// access. Routing it through the instance overload dropped the type arguments, which is
    /// why a generic factory inside a module could not be closed over anything.
    /// </remarks>
    private ValueTask<InvocationResult> InvokeResolvedTargetAsync(
        object target,
        string methodName,
        IReadOnlyList<object?> arguments,
        IReadOnlyList<Type>? typeArguments,
        CancellationToken cancellationToken)
    {
        if (typeArguments is { Count: > 0 } && target is IShellStaticType staticType)
        {
            return LanguageRuntime.Invoker.InvokeStaticMethodAsync(
                staticType, methodName, arguments, cancellationToken, typeArguments);
        }

        return LanguageRuntime.Invoker.InvokeInstanceMethodAsync(
            target, methodName, arguments, cancellationToken);
    }

    private bool TryResolveShellSymbolAccess(string path, out object? value)
    {
        if (TryGetNamedType(path, out var directType))
        {
            value = directType;
            return true;
        }

        var segments = SplitQualifiedPath(path);

        // The same rule as `TryPlanShellSymbol` — see `TS-P2-100` there. A bare
        // module name still resolves to the module; only a dotted path whose next
        // segment the module does not export is left for CLR resolution.
        if (segments.Length >= 1 &&
            TryGetModule(segments[0], out var module) &&
            (segments.Length == 1 || ModuleClaimsPath(module, segments[0], segments[1])))
        {
            value = segments.Length == 1
                ? module
                : LanguageRuntime.ObjectAccessor.GetValue(module, string.Join('.', segments[1..]));
            return true;
        }

        if (segments.Length == 2 &&
            TryGetNamedType(segments[0], out var shellType))
        {
            value = LanguageRuntime.Invoker.GetStaticMember(shellType, segments[1]);
            return true;
        }

        // Deeper chains through a declared type: `T.prop.field` used to fall
        // through to the bareword fallback and evaluate to the literal string
        // "T.prop.field", silently. The module branch above has always walked
        // arbitrary depth; this brings declared types in line.
        //
        // Failure falls through rather than propagating, because a longer path
        // may still resolve against a CLR type prefix further down — which is
        // how it behaved before this branch existed.
        // Membership is *tested*, not attempted-and-caught. Catching would
        // swallow a real failure inside a property getter — a thrown value, a
        // NativeError, a cancellation — and report "not found" instead of the
        // cause. Once the head resolves, the rest of the walk propagates:
        // there is no plausible CLR type named `T.prop`, so falling through
        // would only replace a precise error with a vaguer one.
        if (segments.Length > 2 &&
            TryGetNamedType(segments[0], out var chainRoot) &&
            chainRoot.TryGetStaticMember(segments[1], out var current))
        {
            for (var index = 2; index < segments.Length; index++)
            {
                current = LanguageRuntime.ObjectAccessor.GetValue(current, segments[index]);
            }

            value = current;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Rejects an empty member chain. Shared because the message was written out once per
    /// surface, and a duplicated message drifts the moment someone improves one of them.
    /// </summary>
    private static void RequireMemberPath(Type type, IReadOnlyList<string> memberSegments)
    {
        if (memberSegments.Count == 0)
        {
            throw new InvalidOperationException(
                $"No member path was provided for type '{type.FullName}'.");
        }
    }

    private object? ResolveQualifiedMemberChain(Type type, IReadOnlyList<string> memberSegments)
    {
        RequireMemberPath(type, memberSegments);

        object? current = LanguageRuntime.Invoker.GetStaticMember(type, memberSegments[0]);

        for (var index = 1; index < memberSegments.Count; index++)
        {
            current = LanguageRuntime.ObjectAccessor.GetValue(current, memberSegments[index]);
        }

        return current;
    }

    private async ValueTask<object?> ResolveQualifiedMemberChainAsync(
        Type type,
        IReadOnlyList<string> memberSegments,
        CancellationToken cancellationToken)
    {
        RequireMemberPath(type, memberSegments);

        object? current = LanguageRuntime.Invoker.GetStaticMember(type, memberSegments[0]);

        for (var index = 1; index < memberSegments.Count; index++)
        {
            current = await LanguageRuntime.ObjectAccessor.GetValueAsync(
                current,
                memberSegments[index],
                cancellationToken);
        }

        return current;
    }

    private static string[] SplitQualifiedPath(string path)
    {
        return path
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    private static void EnsureBindingNameIsNotReserved(string sourceName, string sourceText, string name, TextSpan span, string titleSuffix)
    {
        if (!RuntimeNamespaceUtilities.IsReservedRuntimeNamespaceName(name))
        {
            return;
        }

        throw ToshDiagnosticException.Create(new ToshDiagnostic(
            Code: "tosh.runtime.reserved_variable_name",
            Title: $"'{name}' is a {titleSuffix}.",
            SourceName: sourceName,
            SourceText: sourceText,
            Span: span,
            Label: $"choose a different name than '{name}'"));
    }

}
