using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Diagnostics.SymbolStore;
using Tosh.Language.Binding;
using Tosh.Compiler.IR;
using Tosh.Language.Parsing;
using Tosh.Runtime;
namespace Tosh.Compiler;

internal sealed partial class EmitterImpl
{
    private bool EnsureBaseClassShellDeclared(string baseName)
    {
        if (_clrTypeShells.ContainsKey(baseName)) return true;
        if (!TryFindDeclaredClassDefinition(baseName, out var baseClass)
            || !CanEmitClrClassShell(baseClass))
        {
            return false;
        }

        DeclareClrClassShell(baseClass);
        return _clrTypeShells.ContainsKey(baseName);
    }

    private void DeclareClrClassShell(BoundClassDefinition cls)
    {
        if (_clrTypeShells.ContainsKey(cls.Name)) return;

        // Generic user-defined classes (e.g. `class Box<T>`) cannot be
        // expressed as a single CLR shell type with substituted
        // properties at compile time. We instead defer entirely to the
        // engine via source-replay (see TypeDefinitionNeedsSourceReplay):
        // skipping shell emission keeps `_clrTypeShells` empty for the
        // class, which causes IsClrShellEmittedTypeDefinition to return
        // false and the registration call to be emitted.
        if (cls.TypeParameters is { Count: > 0 }) return;

        // Locate the (at most one) explicit user-declared constructor.
        // CanEmitClrClassShell rejects primary+explicit coexistence because a
        // single CLR signature cannot preserve both constructor contracts.
        BoundClassConstructorMember? explicitCtor = null;
        foreach (var member in cls.Members)
        {
            if (member is BoundClassConstructorMember c)
            {
                explicitCtor = c;
                break;
            }
        }

        if (!TryAnalyzeClrSuperInitializer(cls, explicitCtor, out var superInitializer))
            return;

        // Resolve the parent TypeBuilder before defining the derived type.
        // If the declared or external base has no compatible shell, leave the
        // entire derived declaration for source replay instead of truncating
        // its hierarchy at System.Object.
        Type parentType = MetadataType(typeof(object));
        ClrTypeShell? baseShell = null;
        if (cls.BaseClassName is not null)
        {
            if (!EnsureBaseClassShellDeclared(cls.BaseClassName)
                || !_clrTypeShells.TryGetValue(cls.BaseClassName, out baseShell))
            {
                // `TOAST-0030`. Not declared here, but possibly a real type all the same —
                // `extends Error`, `extends Exception`. Deriving from it is what the
                // interpreter does, so the emitted type does it too rather than handing the
                // declaration to source replay.
                if (!TryResolveExternalBaseType(cls.BaseClassName, out var externalBase) ||
                    externalBase is null)
                {
                    return;
                }

                parentType = MetadataType(externalBase);
            }
            else
            {

            parentType = baseShell.Type;

            var actualBaseArity = cls.BaseConstructorArgs?.Count
                ?? superInitializer?.Arguments.Count
                ?? 0;
            var expectedBaseArity = baseShell.CtorParamTypes.Length;
            if (actualBaseArity != expectedBaseArity)
            {
                Diagnostics.Add(
                    "tosh.compile.base_constructor_arity_mismatch: "
                    + $"class '{cls.Name}' supplies {actualBaseArity} argument(s) "
                    + $"to base class '{cls.BaseClassName}', whose CLR shell "
                    + $"constructor requires {expectedBaseArity}.");
                return;
            }
            }
        }

        var attrs = TypeAttributes.Public | TypeAttributes.Class;
        if (cls.IsHermit)
        {
            // Hermit classes are static-only; represent them as
            // abstract+sealed in CLR metadata (same shape C# uses).
            attrs |= TypeAttributes.Abstract | TypeAttributes.Sealed;
        }
        else if (cls.IsAbstract)
        {
            // Abstract (hollow) classes cannot be instantiated.
            attrs |= TypeAttributes.Abstract;
        }
        else if (cls.IsSealed)
        {
            // Explicitly-sealed classes cannot be subclassed.
            attrs |= TypeAttributes.Sealed;
        }
        // Non-sealed, non-abstract classes are left without either flag
        // so derived classes can inherit from them at the CLR level.

        var typeBuilder = _moduleBuilder.DefineType(
            $"{_assemblyName}.{MangleClrIdentifier(cls.Name)}",
            attrs,
            parentType);
        StampToshTypeAttribute(typeBuilder, "class", cls.Span);
        StampOriginalNameIfMangled(typeBuilder, cls.Name);

        // Wire up interface implementations declared in this unit.
        if (cls.ImplementedInterfaces is { Count: > 0 })
        {
            foreach (var ifaceName in cls.ImplementedInterfaces)
            {
                if (_clrTypeShells.TryGetValue(ifaceName, out var ifaceShell)
                    && ifaceShell.Type.IsInterface)
                {
                    typeBuilder.AddInterfaceImplementation(ifaceShell.Type);
                }
            }
        }

        // Wire up trait implementations (traits are CLR interfaces with optional DIM bodies).
        if (cls.UsedTraits is { Count: > 0 })
        {
            foreach (var traitName in cls.UsedTraits)
            {
                if (_clrTypeShells.TryGetValue(traitName, out var traitShell)
                    && traitShell.Type.IsInterface)
                {
                    typeBuilder.AddInterfaceImplementation(traitShell.Type);
                }
            }
        }

        // Public mutable instance field per storage property.
        var fields = new Dictionary<string, FieldBuilder>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in cls.Members)
        {
            if (member is BoundClassPropertyMember prop)
            {
                // `TOAST-0038`. A computed property has no storage: its value is produced by
                // the getter every time. Emitting a field for it would be a field nothing
                // ever writes, shadowing the getter for any reader that looks at fields
                // first.
                if (prop.GetterBody is not null) continue;

                if (fields.ContainsKey(prop.Name)) continue;
                var fieldAttrs = MapPropertyVisibility(prop);
                if (prop.IsFixed) fieldAttrs |= FieldAttributes.InitOnly;
                var fb = typeBuilder.DefineField(
                    MangleClrIdentifier(prop.Name),
                    MetadataType(typeof(object)),
                    fieldAttrs);
                StampOriginalNameIfMangled(fb, prop.Name);
                fields[prop.Name] = fb;
            }
        }

        // When the class header has no primary-ctor parameters, the explicit
        // ctor's parameters drive the shell ctor signature (for example,
        // `class Greeter { Greeter(name: string) { ... } }`).
        IReadOnlyList<BoundParameter> ctorSigParams =
            cls.PrimaryConstructorParameters.Count > 0 || explicitCtor is null
                ? cls.PrimaryConstructorParameters
                : explicitCtor.Parameters;

        // Start optimistic: a class without methods, or one whose
        // every method we can lower to real IL on the shell, supports
        // direct newobj. We flip back to host-dispatch newobj when
        // we encounter a member shape we can't represent on the
        // shell (static/abstract methods, named/optional/rest params,
        // captures, inheritance, computed props, etc.) — those still
        // need the engine-side ToshClassObject to own dispatch.
        var supportsDirectNewObj = true;
        // Inheritance now emits a complete CLR shell hierarchy, but an
        // General inherited construction remains host-dispatched until inherited member
        // reads stay typed. Error subclasses are the narrow exception: their CLR base is
        // complete, their own state is emitted, and throwing/catching them needs no
        // inherited dynamic member lookup.
        // Traits may carry property default values that are set by the tosh
        // evaluator during ToshHost.CreateObject — bypassing that path with
        // a bare newobj would silently drop those defaults.
        if ((cls.BaseClassName is not null
                && !typeof(ToshError).IsAssignableFrom(parentType))
            || (cls.UsedTraits is { Count: > 0 })
            || (cls.ImplementedInterfaces is { Count: > 0 })
            || cls.IsAbstract
            || cls.IsHermit)
        {
            supportsDirectNewObj = false;
        }

        // Constructor matching the chosen ctor signature; each
        // parameter is `object`. For each parameter that names a
        // declared property (case-insensitive), copy the parameter
        // value into the backing field. Other parameters are ignored
        // by the shell ctor's prologue but remain visible to a
        // lowered explicit-ctor body via _paramSlots.
        var paramTypes = new Type[ctorSigParams.Count];
        for (var i = 0; i < paramTypes.Length; i++) paramTypes[i] = MetadataType(typeof(object));
        var ctor = typeBuilder.DefineConstructor(
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            CallingConventions.Standard,
            paramTypes);
        for (var i = 0; i < ctorSigParams.Count; i++)
        {
            ctor.DefineParameter(i + 1, ParameterAttributes.None, ctorSigParams[i].Name);
        }
        var ctorIl = ctor.GetILGenerator();
        // Call the base constructor. Header arguments and a leading direct
        // `$super(args)` are two source spellings for the same CLR initializer;
        // both are evaluated in the selected constructor's parameter scope.
        ctorIl.Emit(OpCodes.Ldarg_0);
        if (baseShell is not null)
        {
            if (cls.BaseConstructorArgs is not null || superInitializer is not null)
            {
                var savedIl = _il;
                var savedLocals = _locals;
                var savedParams = _paramSlots;
                var savedTypedLocals = _typedParamLocals;
                var savedReturnType = _currentFunctionReturnType;
                var savedThis = _currentThisType;
                try
                {
                    _il = ctorIl;
                    _locals = new();
                    _paramSlots = new();
                    _typedParamLocals = new();
                    _currentFunctionReturnType = null;
                    _currentThisType = typeBuilder;

                    for (var i = 0; i < ctorSigParams.Count; i++)
                        _paramSlots[ctorSigParams[i].Symbol] = i + 1;

                    if (cls.BaseConstructorArgs is not null)
                    {
                        foreach (var baseArg in cls.BaseConstructorArgs)
                        {
                            if (TryResolveCtorInitializerParameterSlot(baseArg, ctorSigParams, out var paramSlot))
                            {
                                ctorIl.Emit(OpCodes.Ldarg, paramSlot);
                                continue;
                            }

                            var baseArgType = EmitPipeline(baseArg, asStatement: false);
                            if (baseArgType is null)
                            {
                                // Preserve a verifiable stack after the
                                // expression emitter records its diagnostic.
                                ctorIl.Emit(OpCodes.Ldnull);
                            }
                            else
                            {
                                BoxIfValueType(baseArgType);
                            }
                        }
                    }
                    else
                    {
                        foreach (var baseArg in superInitializer!.Arguments)
                        {
                            var baseArgType = EmitExpression(baseArg.Value);
                            if (baseArgType is null)
                            {
                                // Preserve a verifiable stack after the
                                // expression emitter records its diagnostic.
                                ctorIl.Emit(OpCodes.Ldnull);
                            }
                            else
                            {
                                BoxIfValueType(baseArgType);
                            }
                        }
                    }
                }
                finally
                {
                    _il = savedIl;
                    _locals = savedLocals;
                    _paramSlots = savedParams;
                    _typedParamLocals = savedTypedLocals;
                    _currentFunctionReturnType = savedReturnType;
                    _currentThisType = savedThis;
                }
            }
            ctorIl.Emit(OpCodes.Call, baseShell.Ctor);
        }
        else
        {
            // `TOAST-0030`. The *parent's* constructor, which is `object`'s only when there
            // is no base. An external base — `Error`, `Exception` — is chained to here, and
            // `TryResolveExternalBaseType` is what guarantees the parameterless constructor
            // this looks up actually exists.
            ctorIl.Emit(OpCodes.Call, parentType.GetConstructor(Type.EmptyTypes)!);
        }

        // CLR requires the base constructor call to be the first operation.
        // Once it has completed, guard all lowered initializer and constructor
        // body work so recursive direct `new` expressions fail as a Tosh
        // diagnostic rather than overflowing the CLR stack.
        var constructorExecutionFrame = ctorIl.DeclareLocal(typeof(IDisposable));
        EmitEnterExecutionFrameCall(ctorIl, $"class {cls.Name}.ctor");
        ctorIl.Emit(OpCodes.Stloc, constructorExecutionFrame);
        ctorIl.BeginExceptionBlock();

        // CLR constructor parameters are object-typed on class shells, so every source
        // annotation is otherwise erased. Convert once into locals before a field,
        // initializer, or explicit constructor body can observe the arguments.
        var validatedCtorParameters = new LocalBuilder?[ctorSigParams.Count];
        for (var i = 0; i < ctorSigParams.Count; i++)
        {
            var parameter = ctorSigParams[i];
            if (parameter.TypeName is not { } parameterTypeName) continue;

            var validated = ctorIl.DeclareLocal(typeof(object));
            ctorIl.Emit(OpCodes.Ldarg, i + 1);
            EmitCheckCallableParameter(
                ctorIl,
                parameterTypeName,
                parameter.Span,
                "constructor",
                cls.Name,
                parameter.Name,
                ctorSigParams.Count);
            ctorIl.Emit(OpCodes.Stloc, validated);
            validatedCtorParameters[i] = validated;
        }

        for (var i = 0; i < ctorSigParams.Count; i++)
        {
            var pname = ctorSigParams[i].Name;
            if (!fields.TryGetValue(pname, out var fb)) continue;
            ctorIl.Emit(OpCodes.Ldarg_0);
            if (validatedCtorParameters[i] is { } validated)
                ctorIl.Emit(OpCodes.Ldloc, validated);
            else
                ctorIl.Emit(OpCodes.Ldarg, i + 1);
            ctorIl.Emit(OpCodes.Stfld, fb);
        }

        // Apply property initializer expressions on the CLR ctor path so
        // default member values are preserved without source replay.
        if (cls.Members.OfType<BoundClassPropertyMember>().Any(static p => p.Initializer is not null))
        {
            var savedIl = _il;
            var savedLocals = _locals;
            var savedParams = _paramSlots;
            var savedTypedLocals = _typedParamLocals;
            var savedReturnType = _currentFunctionReturnType;
            var savedThis = _currentThisType;
            try
            {
                _il = ctorIl;
                _locals = new();
                _paramSlots = new();
                _typedParamLocals = new();
                _currentFunctionReturnType = null;
                _currentThisType = typeBuilder;

                for (var i = 0; i < ctorSigParams.Count; i++)
                {
                    if (validatedCtorParameters[i] is { } validated)
                        _typedParamLocals[ctorSigParams[i].Symbol] = validated;
                    else
                        _paramSlots[ctorSigParams[i].Symbol] = i + 1;
                }

                foreach (var prop in cls.Members.OfType<BoundClassPropertyMember>())
                {
                    if (prop.Initializer is null) continue;
                    if (!fields.TryGetValue(prop.Name, out var fb)) continue;

                    ctorIl.Emit(OpCodes.Ldarg_0);
                    if (TryResolveCtorInitializerParameterSlot(prop.Initializer, ctorSigParams, out var paramSlot))
                    {
                        if (validatedCtorParameters[paramSlot - 1] is { } validated)
                            ctorIl.Emit(OpCodes.Ldloc, validated);
                        else
                            ctorIl.Emit(OpCodes.Ldarg, paramSlot);
                    }
                    else
                    {
                        var initType = EmitPipeline(prop.Initializer, asStatement: false);
                        if (initType is null)
                        {
                            ctorIl.Emit(OpCodes.Ldnull);
                            supportsDirectNewObj = false;
                        }
                        else
                        {
                            BoxIfValueType(initType);
                        }
                    }

                    ctorIl.Emit(OpCodes.Stfld, fb);
                }
            }
            finally
            {
                _il = savedIl;
                _locals = savedLocals;
                _paramSlots = savedParams;
                _typedParamLocals = savedTypedLocals;
                _currentFunctionReturnType = savedReturnType;
                _currentThisType = savedThis;
            }
        }

        // Lower the explicit ctor body (if any) inline. Statements
        // are net-zero stack so we can append straight to ctorIl,
        // then emit Ret. _currentThisType is set so $this resolves;
        // _paramSlots maps each parameter symbol to its arg slot.
        var constructorReturnEpilogueEmitted = false;
        if (explicitCtor is not null)
        {
            var savedIl = _il;
            var savedLocals = _locals;
            var savedParams = _paramSlots;
            var savedTypedLocals = _typedParamLocals;
            var savedReturnType = _currentFunctionReturnType;
            var savedThis = _currentThisType;
            var savedReturnEmissionFrame = _returnEmissionFrame;
            var savedDeferredCleanupFrames = _deferredCleanupFrames;
            try
            {
                _il = ctorIl;
                _locals = new();
                _paramSlots = new();
                _typedParamLocals = new();
                _currentFunctionReturnType = null;
                _currentThisType = typeBuilder;
                _deferredCleanupFrames = new();
                // When the primary ctor drives the signature but the
                // explicit ctor's parameters need to be visible to
                // its body, they don't have arg slots. In that case
                // we currently don't support body lowering — guarded
                // by ctorSigParams == explicitCtor.Parameters above.
                if (ReferenceEquals(ctorSigParams, explicitCtor.Parameters))
                {
                    for (var i = 0; i < explicitCtor.Parameters.Count; i++)
                    {
                        if (validatedCtorParameters[i] is { } validated)
                            _typedParamLocals[explicitCtor.Parameters[i].Symbol] = validated;
                        else
                            _paramSlots[explicitCtor.Parameters[i].Symbol] = i + 1;
                    }
                    IReadOnlyList<BoundStatement> bodyStatements = superInitializer is null
                        ? explicitCtor.Body.Statements
                        : explicitCtor.Body.Statements.Skip(1).ToArray();
                    var bodyBlock = CreateSyntheticBlock(
                        bodyStatements,
                        explicitCtor.Body.Span);
                    var returnFrame = CreateReturnEmissionFrame(typeof(void));
                    _returnEmissionFrame = returnFrame;
                    EmitBlock(bodyBlock);
                    // Falling through a constructor body must leave the
                    // protected region just like an explicit source return.
                    ctorIl.Emit(OpCodes.Leave, returnFrame.Epilogue);
                    ctorIl.BeginFinallyBlock();
                    ctorIl.Emit(OpCodes.Ldloc, constructorExecutionFrame);
                    ctorIl.Emit(OpCodes.Callvirt, s_executionFrameDispose);
                    ctorIl.EndExceptionBlock();
                    EmitReturnEpilogue(returnFrame);
                    constructorReturnEpilogueEmitted = true;
                }
                // else: explicit ctor coexists with primary ctor; its
                // parameters aren't bound to CLR args. Leave body
                // unlowered for now — host dispatch still owns it.
                else
                {
                    supportsDirectNewObj = false;
                }
            }
            finally
            {
                _il = savedIl;
                _locals = savedLocals;
                _paramSlots = savedParams;
                _typedParamLocals = savedTypedLocals;
                _currentFunctionReturnType = savedReturnType;
                _currentThisType = savedThis;
                _returnEmissionFrame = savedReturnEmissionFrame;
                _deferredCleanupFrames = savedDeferredCleanupFrames;
            }
        }
        if (!constructorReturnEpilogueEmitted)
        {
            var epilogue = ctorIl.DefineLabel();
            ctorIl.Emit(OpCodes.Leave, epilogue);
            ctorIl.BeginFinallyBlock();
            ctorIl.Emit(OpCodes.Ldloc, constructorExecutionFrame);
            ctorIl.Emit(OpCodes.Callvirt, s_executionFrameDispose);
            ctorIl.EndExceptionBlock();
            ctorIl.MarkLabel(epilogue);
            ctorIl.Emit(OpCodes.Ret);
        }

        var paramNames = new string[ctorSigParams.Count];
        for (var i = 0; i < paramNames.Length; i++) paramNames[i] = ctorSigParams[i].Name;

        // Build a set of method names that must be virtual because they
        // implement a method declared on an interface that this class
        // claims to implement. The CLR verifier requires virtual methods
        // for DefineMethodOverride to work.
        var interfaceMethodNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (cls.ImplementedInterfaces is { Count: > 0 })
        {
            foreach (var ifaceName in cls.ImplementedInterfaces)
            {
                if (_clrTypeShells.TryGetValue(ifaceName, out var ifaceShell)
                    && ifaceShell.Type.IsInterface)
                {
                    foreach (var methodName in ifaceShell.Methods.Keys)
                        interfaceMethodNames.Add(methodName);
                }
            }
        }

        // Also collect trait method names so class methods that implement
        // trait abstract methods are marked virtual for DefineMethodOverride.
        if (cls.UsedTraits is { Count: > 0 })
        {
            foreach (var traitName in cls.UsedTraits)
            {
                if (_clrTypeShells.TryGetValue(traitName, out var traitShell)
                    && traitShell.Type.IsInterface)
                {
                    foreach (var methodName in traitShell.Methods.Keys)
                        interfaceMethodNames.Add(methodName);
                }
            }
        }

        // First pass: declare MethodBuilders for every lowerable
        // method. We collect into a side list so the body emit can
        // happen after Program is finalized — same pattern as the
        // CLR module methods.
        var pendingMethods = new List<(BoundClassMethodMember Member, MethodBuilder Builder)>();
        var methods = new Dictionary<string, MethodBuilder>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in cls.Members)
        {
            if (member is BoundClassMethodMember m)
            {
                // Abstract methods get an abstract MethodBuilder stub with no body.
                if (m.IsAbstract)
                {
                    var abstractArity = m.Method.Parameters.Count;
                    var abstractParamTypes = new Type[abstractArity];
                    for (var i = 0; i < abstractArity; i++) abstractParamTypes[i] = MetadataType(typeof(object));
                    var mbAbstract = typeBuilder.DefineMethod(
                        MangleClrIdentifier(ToClrOperatorName(m.Method.Name)),
                        MapMethodVisibility(m) | MethodAttributes.HideBySig
                            | MethodAttributes.Virtual | MethodAttributes.NewSlot
                            | MethodAttributes.Abstract,
                        CallingConventions.HasThis,
                        returnType: MetadataType(typeof(object)),
                        parameterTypes: abstractParamTypes);
                    StampOriginalNameIfMangled(mbAbstract, m.Method.Name);
                    for (var i = 0; i < abstractArity; i++)
                        mbAbstract.DefineParameter(i + 1, ParameterAttributes.None, m.Method.Parameters[i].Name);
                    methods[m.Method.Name] = mbAbstract;
                    supportsDirectNewObj = false;
                    continue;
                }

                if (!CanLowerClassMethod(m))
                {
                    supportsDirectNewObj = false;
                    continue;
                }
                if (methods.ContainsKey(m.Method.Name))
                {
                    // Defensive — duplicate method names shouldn't
                    // exist after lowering, but if they do, leave
                    // dispatch to the host.
                    supportsDirectNewObj = false;
                    continue;
                }
                var arity = m.Method.Parameters.Count;
                var mParamTypes = new Type[arity];
                for (var i = 0; i < arity; i++) mParamTypes[i] = MetadataType(typeof(object));
                var isStaticMethod = m.IsStatic;
                var methodAttrs = MapMethodVisibility(m) | MethodAttributes.HideBySig;
                if (isStaticMethod)
                    methodAttrs |= MethodAttributes.Static;
                else if (m.IsOverride)
                    // ReuseSlot (Virtual without NewSlot) — reuses the base class vtable slot.
                    methodAttrs |= MethodAttributes.Virtual;
                else
                    // NewSlot — open a fresh vtable slot so subclasses can override via ReuseSlot
                    // and so DefineMethodOverride works for interface/trait implementations.
                    methodAttrs |= MethodAttributes.Virtual | MethodAttributes.NewSlot;
                var callingConvention = isStaticMethod
                    ? CallingConventions.Standard
                    : CallingConventions.HasThis;
                var mb = typeBuilder.DefineMethod(
                    MangleClrIdentifier(ToClrOperatorName(m.Method.Name)),
                    methodAttrs,
                    callingConvention,
                    returnType: MetadataType(typeof(object)),
                    parameterTypes: mParamTypes);
                StampOriginalNameIfMangled(mb, m.Method.Name);
                for (var i = 0; i < arity; i++)
                {
                    mb.DefineParameter(i + 1, ParameterAttributes.None, m.Method.Parameters[i].Name);
                }
                if (!isStaticMethod)
                    methods[m.Method.Name] = mb;
                pendingMethods.Add((m, mb));
            }
            else if (member is BoundClassPropertyMember prop)
            {
                // Getter bodies are real CLR properties (DeclareComputedProperties below),
                // so they do not prevent direct construction. Setter bodies and lazy
                // storage still need engine-owned behavior.
                if (prop.SetterBody is not null || prop.IsLazy)
                {
                    supportsDirectNewObj = false;
                }
            }
            else if (member is BoundClassConstructorMember)
            {
                // The (single) explicit ctor is lowered inline by the
                // ctor IL above when it drives the shell signature.
                // When a primary ctor co-exists, supportsDirectNewObj
                // was already disabled by that path.
            }
            else if (member is BoundClassEventMember eventMember)
            {
                EmitClassEventMemberInfrastructure(typeBuilder, eventMember);
            }
        }
        var shell = new ClrTypeShell(cls.Name, typeBuilder, ctor, paramTypes, paramNames, fields, supportsDirectNewObj: supportsDirectNewObj);
        foreach (var (k, v) in methods) shell.Methods[k] = v;

        // For each interface this class implements, link matching method
        // implementations via DefineMethodOverride so the CLR verifier
        // accepts the type even when the implementing method is not virtual.
        if (cls.ImplementedInterfaces is { Count: > 0 })
        {
            foreach (var ifaceName in cls.ImplementedInterfaces)
            {
                if (!_clrTypeShells.TryGetValue(ifaceName, out var ifaceShell)
                    || !ifaceShell.Type.IsInterface)
                    continue;
                foreach (var (methodName, ifaceMethod) in ifaceShell.Methods)
                {
                    if (methods.TryGetValue(methodName, out var implMethod))
                        typeBuilder.DefineMethodOverride(implMethod, ifaceMethod);
                }
            }
        }

        // For each trait this class uses, link matching method overrides.
        // Methods declared abstract on the trait must be provided by the class;
        // DIM methods are inherited automatically but still need DefineMethodOverride
        // when the class provides its own implementation.
        if (cls.UsedTraits is { Count: > 0 })
        {
            foreach (var traitName in cls.UsedTraits)
            {
                if (!_clrTypeShells.TryGetValue(traitName, out var traitShell)
                    || !traitShell.Type.IsInterface)
                    continue;
                foreach (var (methodName, traitMethod) in traitShell.Methods)
                {
                    if (methods.TryGetValue(methodName, out var implMethod))
                        typeBuilder.DefineMethodOverride(implMethod, traitMethod);
                }
            }
        }

        // For overrule (override) methods, wire DefineMethodOverride to the corresponding
        // base class virtual slot so the CLR emits true polymorphic dispatch metadata.
        // Without this, callvirt through a base-typed reference could hit the wrong method.
        if (baseShell is not null)
        {
            foreach (var (overrideMember, overrideBuilder) in pendingMethods)
            {
                if (overrideMember.IsOverride
                    && baseShell.Methods.TryGetValue(overrideMember.Method.Name, out var baseMethod))
                {
                    typeBuilder.DefineMethodOverride(overrideBuilder, baseMethod);
                }
            }
        }

        _clrTypeShells[cls.Name] = shell;
        _clrShellsByType[typeBuilder] = shell;
        foreach (var (member, builder) in pendingMethods)
        {
            _clrClassMethodBodies.Add(new ClrClassMethodPending(shell, builder, member.Method));
        }

        DeclareComputedProperties(cls, typeBuilder, shell);
        DeclareDefaultToString(cls, typeBuilder);
    }

    /// <summary>
    /// Emits the <c>ToString</c> an emitted class would otherwise inherit — <c>TOAST-0065</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// `ToshClassInstance.ToString()` answers a declared `ToString` if there is one and the
    /// class's name otherwise. An emitted class inherited `object.ToString()`, which answers
    /// the CLR type's full name — so `$s as string` was `Circle` interpreted and `p.Circle`
    /// compiled, carrying the assembly's namespace into a value the reader wrote.
    /// </para>
    /// <para>
    /// It reaches further than `as string`: equality converts, so `$s == "Circle"` was true
    /// interpreted and false compiled, and a `match` value arm spelled `Circle => …` followed
    /// it. That arm is what `TOAST-0065` recorded, which is why the item reads as a `match`
    /// defect — the spec's type pattern, `_ is Circle`, worked on both backends all along.
    /// </para>
    /// <para>
    /// Marked compiler-generated so the renderer can tell it from a `ToString` the author
    /// wrote: one is a rendering declaration to be preferred over structural output, and this
    /// is the fallback that structural output exists to beat.
    /// </para>
    /// </remarks>
    private void DeclareDefaultToString(BoundClassDefinition cls, TypeBuilder typeBuilder)
    {
        var declaresOwn = cls.Members
            .OfType<BoundClassMethodMember>()
            .Any(static member => string.Equals(
                member.Method.Name, nameof(ToString), StringComparison.OrdinalIgnoreCase));

        if (declaresOwn)
        {
            return;
        }

        var builder = typeBuilder.DefineMethod(
            nameof(ToString),
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            MetadataType(typeof(string)),
            Type.EmptyTypes);

        builder.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute)
                .GetConstructor(Type.EmptyTypes)!,
            Array.Empty<object>()));

        var il = builder.GetILGenerator();
        il.Emit(OpCodes.Ldstr, cls.Name);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits a CLR property per computed tosh property — <c>TOAST-0038</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// `prop Label: string => $"at offset {$this.Position}"` becomes a real property with a
    /// getter and no backing field, so reflection — which is how a compiled instance's
    /// members are read — finds it in the ordinary way.
    /// </para>
    /// <para>
    /// The body is driven through the shared method emitter by wrapping it in a synthetic
    /// <see cref="BoundFunctionDefinition"/> of no parameters, the same device trait default
    /// bodies use. That also means it gets
    /// <c>CollapseTrailingExpressionIntoReturn</c> for free, which is what makes an
    /// expression body return its value rather than fall through to `return null`.
    /// </para>
    /// </remarks>
    private void DeclareComputedProperties(
        BoundClassDefinition cls,
        TypeBuilder typeBuilder,
        ClrTypeShell shell)
    {
        foreach (var member in cls.Members)
        {
            if (member is not BoundClassPropertyMember { GetterBody: not null } prop)
            {
                continue;
            }

            var mangled = MangleClrIdentifier(prop.Name);

            var getter = typeBuilder.DefineMethod(
                $"get_{mangled}",
                MethodAttributes.Public | MethodAttributes.HideBySig
                    | MethodAttributes.SpecialName | MethodAttributes.Virtual,
                MetadataType(typeof(object)),
                Type.EmptyTypes);

            var property = typeBuilder.DefineProperty(
                mangled,
                PropertyAttributes.None,
                MetadataType(typeof(object)),
                Type.EmptyTypes);
            property.SetGetMethod(getter);

            if (MangleClrIdentifier(prop.Name) != prop.Name)
            {
                property.SetCustomAttribute(new CustomAttributeBuilder(
                    s_toshOriginalNameCtor, new object[] { prop.Name }));
            }

            var syntheticGetter = new BoundFunctionDefinition(
                Name: prop.Name,
                Symbol: new BoundSymbol(prop.Name, BoundSymbolKind.Parameter, ScopeDepth: 0, DeclaredType: BoundType.Dynamic),
                Parameters: Array.Empty<BoundParameter>(),
                ReturnTypeName: prop.TypeName,
                Body: prop.GetterBody,
                Captures: Array.Empty<BoundSymbol>(),
                IsCommandWrapper: false,
                Modifier: DeclarationModifier.Default,
                Span: prop.Span);

            _clrClassMethodBodies.Add(new ClrClassMethodPending(shell, getter, syntheticGetter));
        }
    }

    /// <summary>
    /// Validate and extract the compatibility-form constructor initializer
    /// `$super(args)`. Only a plain, foreground, one-stage call can be
    /// normalized; `$super.member()` is a <see cref="BoundMethodCall"/> and is
    /// intentionally left in the ordinary expression/dispatch path.
    /// </summary>
    private bool TryAnalyzeClrSuperInitializer(
        BoundClassDefinition cls,
        BoundClassConstructorMember? explicitCtor,
        out BoundCallableInvocation? initializer)
    {
        initializer = null;
        if (explicitCtor is null)
            return true;

        var calls = new List<(int Index, BoundCallableInvocation Invocation)>();
        for (var i = 0; i < explicitCtor.Body.Statements.Count; i++)
        {
            if (TryGetDirectSuperConstructorCall(
                    explicitCtor.Body.Statements[i],
                    out var invocation))
            {
                calls.Add((i, invocation));
            }
        }

        if (calls.Count > 1)
        {
            Diagnostics.Add(
                "tosh.compile.duplicate_base_constructor_initializer: "
                + $"constructor '{cls.Name}()' calls '$super(...)' more than once.");
            return false;
        }

        if (calls.Count == 0)
            return true;

        var call = calls[0];
        if (call.Index != 0)
        {
            Diagnostics.Add(
                "tosh.compile.super_initializer_must_be_first: "
                + $"'$super(...)' must be the first executable statement "
                + $"in constructor '{cls.Name}()'.");
            return false;
        }

        if (cls.BaseConstructorArgs is not null)
        {
            Diagnostics.Add(
                "tosh.compile.duplicate_base_constructor_initializer: "
                + $"class '{cls.Name}' cannot combine 'extends "
                + $"{cls.BaseClassName}(...)' arguments with '$super(...)'.");
            return false;
        }

        if (cls.BaseClassName is null)
        {
            Diagnostics.Add(
                "tosh.compile.super_without_base_class: "
                + $"class '{cls.Name}' cannot call '$super(...)' because "
                + "it has no base class.");
            return false;
        }

        if (call.Invocation.Arguments.Any(static argument =>
                argument.Name is not null || argument.IsSplat))
        {
            Diagnostics.Add(
                "tosh.compile.unsupported_super_initializer_argument: "
                + $"constructor '{cls.Name}()' must use positional, "
                + "non-splat arguments in '$super(...)' when emitting a CLR shell.");
            return false;
        }

        initializer = call.Invocation;
        return true;
    }

    private static bool TryGetDirectSuperConstructorCall(
        BoundStatement statement,
        out BoundCallableInvocation invocation)
    {
        invocation = null!;
        if (statement is not BoundPipelineStatement pipelineStatement
            || pipelineStatement.Pipeline.Stages.Count != 1
            || pipelineStatement.Pipeline.Stages[0] is not BoundExpressionStage
            {
                Value: BoundCallableInvocation
                {
                    Target: BoundVariableReference { Name: var name },
                } call,
            }
            || !string.Equals(name, "super", StringComparison.OrdinalIgnoreCase)
            || pipelineStatement.Pipeline.BoundRedirections.Count > 0
            || pipelineStatement.Pipeline.BoundInputRedirection is not null
            || pipelineStatement.Pipeline.Original is PipelineSyntax { IsBackground: true })
        {
            return false;
        }

        invocation = call;
        return true;
    }

    private static bool TryResolveCtorInitializerParameterSlot(
        BoundPipeline initializer,
        IReadOnlyList<BoundParameter> ctorSigParams,
        out int slot)
    {
        slot = -1;
        if (initializer.Stages.Count != 1)
            return false;

        var stage = initializer.Stages[0];

        if (stage is BoundExpressionStage { Value: BoundVariableReference vr })
        {
            for (var i = 0; i < ctorSigParams.Count; i++)
            {
                if (ReferenceEquals(ctorSigParams[i].Symbol, vr.Symbol))
                {
                    slot = i + 1;
                    return true;
                }
            }
        }

        if (stage is BoundCommandCall { Arguments.Count: 0 } call)
        {
            for (var i = 0; i < ctorSigParams.Count; i++)
            {
                if (string.Equals(ctorSigParams[i].Name, call.Name, StringComparison.OrdinalIgnoreCase))
                {
                    slot = i + 1;
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Predicate: can this class method's body be lowered to real
    /// IL on the class shell? Conservative — falls back to engine
    /// dispatch for anything we don't yet model on the IL side.
    /// </summary>
    private static bool CanLowerClassMethod(BoundClassMethodMember m)
    {
        // Abstract methods have no body — they're emitted as abstract
        // stubs in DeclareClrClassShell and not added to pendingMethods.
        if (m.IsAbstract) return false;
        // Static methods, overrides, and new instance methods are all
        // supported on class shells.
        if (m.Method.Captures.Count > 0) return false;  // closures over outer scope
        foreach (var p in m.Method.Parameters)
        {
            // Defaulted parameters need the engine's callable default
            // binder (TS-P1-05): a fixed-arity CLR method cannot bind a
            // call that omits them.
            if (p.IsRest || p.IsOptional || p.Default is not null) return false;
        }
        return true;
    }

    /// <summary>
    /// Emit the CLR infrastructure for one class-member event declaration:
    /// a private backing field of type <c>Action&lt;object&gt;</c>, a
    /// public <c>add_X</c> method that appends a handler via
    /// <see cref="Delegate.Combine"/>, a public <c>remove_X</c> method
    /// that drops a handler via <see cref="Delegate.Remove"/>, and an
    /// <see cref="EventBuilder"/> that links the two accessors so the
    /// event is reflectable as a standard CLR event.
    /// </summary>
    private void EmitClassEventMemberInfrastructure(TypeBuilder typeBuilder, BoundClassEventMember ev)
    {
        var handlerType = MetadataType(typeof(Action<object>));
        var backingFieldName = "_event_" + MangleClrIdentifier(ev.Name);
        var backingField = typeBuilder.DefineField(
            backingFieldName,
            handlerType,
            ev.IsShy ? FieldAttributes.Private : FieldAttributes.Private); // always private

        // add_X(Action<object> value): backing = (Action<object>?)Delegate.Combine(backing, value)
        var addAttrs = MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig;
        var addMethod = typeBuilder.DefineMethod(
            "add_" + MangleClrIdentifier(ev.Name),
            addAttrs,
            CallingConventions.HasThis,
            MetadataType(typeof(void)),
            new[] { handlerType });
        addMethod.DefineParameter(1, ParameterAttributes.None, "value");
        var addIl = addMethod.GetILGenerator();
        var addLocal = addIl.DeclareLocal(handlerType);
        addIl.Emit(OpCodes.Ldarg_0);
        addIl.Emit(OpCodes.Ldfld, backingField);
        addIl.Emit(OpCodes.Ldarg_1);
        addIl.Emit(OpCodes.Call, s_delegateCombine);
        addIl.Emit(OpCodes.Isinst, handlerType);
        addIl.Emit(OpCodes.Stloc, addLocal);
        addIl.Emit(OpCodes.Ldarg_0);
        addIl.Emit(OpCodes.Ldloc, addLocal);
        addIl.Emit(OpCodes.Stfld, backingField);
        addIl.Emit(OpCodes.Ret);

        // remove_X(Action<object> value): backing = (Action<object>?)Delegate.Remove(backing, value)
        var removeAttrs = MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig;
        var removeMethod = typeBuilder.DefineMethod(
            "remove_" + MangleClrIdentifier(ev.Name),
            removeAttrs,
            CallingConventions.HasThis,
            MetadataType(typeof(void)),
            new[] { handlerType });
        removeMethod.DefineParameter(1, ParameterAttributes.None, "value");
        var removeIl = removeMethod.GetILGenerator();
        var removeLocal = removeIl.DeclareLocal(handlerType);
        removeIl.Emit(OpCodes.Ldarg_0);
        removeIl.Emit(OpCodes.Ldfld, backingField);
        removeIl.Emit(OpCodes.Ldarg_1);
        removeIl.Emit(OpCodes.Call, s_delegateRemove);
        removeIl.Emit(OpCodes.Isinst, handlerType); // null-safe: Delegate.Remove can return null
        removeIl.Emit(OpCodes.Stloc, removeLocal);
        removeIl.Emit(OpCodes.Ldarg_0);
        removeIl.Emit(OpCodes.Ldloc, removeLocal);
        removeIl.Emit(OpCodes.Stfld, backingField);
        removeIl.Emit(OpCodes.Ret);

        // Wire up the EventBuilder so the event is reflectable.
        var eb = typeBuilder.DefineEvent(MangleClrIdentifier(ev.Name), EventAttributes.None, handlerType);
        eb.SetAddOnMethod(addMethod);
        eb.SetRemoveOnMethod(removeMethod);
    }

    /// <summary>
    /// Emits the deferred body of every class method declared during
    /// <see cref="DeclareClrClassShell"/>. Mirrors the pattern used
    /// by <see cref="EmitClrModuleMethodBodies"/> but reserves
    /// <c>arg 0</c> for <c>this</c> (typed as the shell's
    /// <see cref="TypeBuilder"/>), and exposes that slot to the
    /// expression emitter via <see cref="_currentThisType"/> so
    /// <c>$this</c> references resolve correctly.
    /// </summary>
    private void EmitClrClassMethodBodies()
    {
        foreach (var pending in _clrClassMethodBodies)
        {
            var savedIl = _il;
            var savedLocals = _locals;
            var savedParams = _paramSlots;
            var savedTypedLocals = _typedParamLocals;
            var savedReturnType = _currentFunctionReturnType;
            var savedThis = _currentThisType;
            var savedReturnEmissionFrame = _returnEmissionFrame;
            var savedDeferredCleanupFrames = _deferredCleanupFrames;
            try
            {
                _il = pending.Method.GetILGenerator();
                _locals = new();
                _paramSlots = new();
                _typedParamLocals = new();
                _currentFunctionReturnType = null;       // object-typed return
                var isStaticMethod = pending.Method.IsStatic;
                _currentThisType = isStaticMethod ? null : pending.Shell.Type;
                _deferredCleanupFrames = new();
                var returnFrame = CreateReturnEmissionFrame(typeof(object));
                _returnEmissionFrame = returnFrame;
                var executionFrame = EmitExecutionFrameEntry(
                    $"class {pending.Shell.Name}.{pending.Definition.Name}");
                // Instance methods: declared params start at slot 1
                // because arg 0 is `this`. Static methods start at 0.
                var argBase = isStaticMethod ? 0 : 1;
                for (var i = 0; i < pending.Definition.Parameters.Count; i++)
                {
                    var parameter = pending.Definition.Parameters[i];
                    if (parameter.TypeName is not { } parameterTypeName)
                    {
                        _paramSlots[parameter.Symbol] = i + argBase;
                        continue;
                    }

                    var validated = _il.DeclareLocal(typeof(object));
                    _il.Emit(OpCodes.Ldarg, i + argBase);
                    EmitCheckCallableParameter(
                        _il,
                        parameterTypeName,
                        parameter.Span,
                        "method",
                        $"{pending.Shell.Name}.{pending.Definition.Name}",
                        parameter.Name,
                        pending.Definition.Parameters.Count);
                    _il.Emit(OpCodes.Stloc, validated);
                    _typedParamLocals[parameter.Symbol] = validated;
                }
                // `TOAST-0043`. The same rule free functions get: a body ending in a bare
                // expression returns it. Without this a class method with an expression
                // body fell through to the implicit `return null` below, so
                // `func M() -> int => 7` answered null compiled and 7 interpreted.
                EmitBlock(CollapseTrailingExpressionIntoReturn(pending.Definition));

                // Fall-through: implicit `return null`.
                _il.Emit(OpCodes.Ldnull);
                _il.Emit(OpCodes.Stloc, returnFrame.ValueLocal!);
                EmitExecutionFrameExit(executionFrame);
                EmitReturnEpilogue(returnFrame);
            }
            finally
            {
                _il = savedIl;
                _locals = savedLocals;
                _paramSlots = savedParams;
                _typedParamLocals = savedTypedLocals;
                _currentFunctionReturnType = savedReturnType;
                _currentThisType = savedThis;
                _returnEmissionFrame = savedReturnEmissionFrame;
                _deferredCleanupFrames = savedDeferredCleanupFrames;
            }
        }
    }

    /// <summary>
    /// Emit a real CLR <c>public sealed class</c> for one tosh
    /// <c>record</c> declaration. Each field becomes a public
    /// mutable instance field typed <c>object</c>. A positional
    /// constructor matching the record's field order is emitted so
    /// <c>new Rec(a, b, c)</c> can be lowered to a direct
    /// <c>newobj</c> in <see cref="EmitNewObject"/>. Default-value
    /// semantics for fields with explicit defaults are still owned
    /// by source-replay (the engine populates them on construction
    /// through its own record machinery); the positional form here
    /// matches the explicit-construction case used by compiled call
    /// sites.
    /// </summary>
    private void DeclareClrRecordShell(BoundRecordDefinition rec)
    {
        if (_clrTypeShells.ContainsKey(rec.Name)) return;

        var attrs = TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed;
        var typeBuilder = _moduleBuilder.DefineType(
            $"{_assemblyName}.{MangleClrIdentifier(rec.Name)}",
            attrs,
            MetadataType(typeof(object)));
        StampToshTypeAttribute(typeBuilder, "record", rec.Span);
        StampOriginalNameIfMangled(typeBuilder, rec.Name);

        var fields = new Dictionary<string, FieldBuilder>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in rec.Fields)
        {
            if (fields.ContainsKey(f.Name)) continue;
            var fb = typeBuilder.DefineField(
                MangleClrIdentifier(f.Name),
                MetadataType(typeof(object)),
                FieldAttributes.Public);
            StampOriginalNameIfMangled(fb, f.Name);
            fields[f.Name] = fb;
        }

        // Positional ctor: one `object` parameter per field, each
        // copied into the matching field. Records are pure data
        // shapes so this is the natural construction shape.
        var paramTypes = new Type[rec.Fields.Count];
        var paramNames = new string[rec.Fields.Count];
        for (var i = 0; i < paramTypes.Length; i++)
        {
            paramTypes[i] = MetadataType(typeof(object));
            paramNames[i] = rec.Fields[i].Name;
        }
        var ctor = typeBuilder.DefineConstructor(
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            CallingConventions.Standard,
            paramTypes);
        for (var i = 0; i < rec.Fields.Count; i++)
        {
            ctor.DefineParameter(i + 1, ParameterAttributes.None, rec.Fields[i].Name);
        }
        var ctorIl = ctor.GetILGenerator();
        ctorIl.Emit(OpCodes.Ldarg_0);
        ctorIl.Emit(OpCodes.Call, MetadataType(typeof(object)).GetConstructor(Type.EmptyTypes)!);
        for (var i = 0; i < rec.Fields.Count; i++)
        {
            if (!fields.TryGetValue(rec.Fields[i].Name, out var fb)) continue;
            ctorIl.Emit(OpCodes.Ldarg_0);
            ctorIl.Emit(OpCodes.Ldarg, i + 1);
            ctorIl.Emit(OpCodes.Stfld, fb);
        }
        ctorIl.Emit(OpCodes.Ret);

        var shell = new ClrTypeShell(rec.Name, typeBuilder, ctor, paramTypes, paramNames, fields, supportsDirectNewObj: true);
        _clrTypeShells[rec.Name] = shell;
        _clrRecordDefinitions[rec.Name] = rec;
        _clrShellsByType[typeBuilder] = shell;
    }

    private static void StampToshTypeAttribute(TypeBuilder typeBuilder, string kind, TextSpan span)
    {
        var ctor = typeof(global::Tosh.Runtime.ToshTypeAttribute)
            .GetConstructor(new[] { typeof(string), typeof(int), typeof(int) })!;
        typeBuilder.SetCustomAttribute(new CustomAttributeBuilder(
            ctor,
            new object[] { kind, span.Start, span.Length }));
    }

    /// <summary>
    /// Map a class/struct property's visibility modifiers to a CLR
    /// <see cref="FieldAttributes"/> visibility flag. Mapping:
    /// <list type="bullet">
    ///   <item><c>shy</c> → <see cref="FieldAttributes.Private"/></item>
    ///   <item><c>guarded</c> → <see cref="FieldAttributes.Family"/> (CLR <c>protected</c>)</item>
    ///   <item><c>local</c> → <see cref="FieldAttributes.Assembly"/> (CLR <c>internal</c>)</item>
    ///   <item>otherwise → <see cref="FieldAttributes.Public"/></item>
    /// </list>
    /// <c>shy</c> wins when stacked with <c>guarded</c>/<c>local</c>; this
    /// matches the evaluator's hide-from-outside-class semantics.
    /// Part of the public CLR ABI v1 (see <c>docs/CLR_ABI_v1.md</c>).
    /// </summary>
    private static FieldAttributes MapPropertyVisibility(BoundClassPropertyMember prop)
    {
        if (prop.IsShy) return FieldAttributes.Private;
        if (prop.IsGuarded) return FieldAttributes.Family;
        if (prop.IsLocal) return FieldAttributes.Assembly;
        return FieldAttributes.Public;
    }

    /// <summary>
    /// Map a class method's visibility modifiers to a CLR
    /// <see cref="MethodAttributes"/> visibility flag. Same precedence
    /// rules as <see cref="MapPropertyVisibility"/>. Part of the
    /// public CLR ABI v1 (see <c>docs/CLR_ABI_v1.md</c>).
    /// </summary>
    private static MethodAttributes MapMethodVisibility(BoundClassMethodMember method)
    {
        if (method.IsShy) return MethodAttributes.Private;
        if (method.IsGuarded) return MethodAttributes.Family;
        if (method.IsLocal) return MethodAttributes.Assembly;
        return MethodAttributes.Public;
    }

    private static void StampToshTypeAttribute(EnumBuilder enumBuilder, string kind, TextSpan span)
    {
        var ctor = typeof(global::Tosh.Runtime.ToshTypeAttribute)
            .GetConstructor(new[] { typeof(string), typeof(int), typeof(int) })!;
        enumBuilder.SetCustomAttribute(new CustomAttributeBuilder(
            ctor,
            new object[] { kind, span.Start, span.Length }));
    }

    private static readonly ConstructorInfo s_toshOriginalNameCtor =
        typeof(global::Tosh.Runtime.ToshOriginalNameAttribute)
            .GetConstructor(new[] { typeof(string) })!;

    /// <summary>
    /// When <paramref name="original"/> would have to be mangled
    /// to land in a valid CLR identifier (i.e. <c>MangleClrIdentifier</c>
    /// returns a different string), stamps the supplied builder
    /// with <c>[ToshOriginalNameAttribute(original)]</c> so tooling
    /// can recover the user's original spelling. No-ops when the
    /// name was already a valid CLR identifier — keeps metadata
    /// lean for the common case.
    /// </summary>
    private static void StampOriginalNameIfMangled(TypeBuilder builder, string original)
    {
        if (MangleClrIdentifier(original) == original) return;
        builder.SetCustomAttribute(new CustomAttributeBuilder(
            s_toshOriginalNameCtor, new object[] { original }));
    }
    private static void StampOriginalNameIfMangled(EnumBuilder builder, string original)
    {
        if (MangleClrIdentifier(original) == original) return;
        builder.SetCustomAttribute(new CustomAttributeBuilder(
            s_toshOriginalNameCtor, new object[] { original }));
    }
    private static void StampOriginalNameIfMangled(FieldBuilder builder, string original)
    {
        if (MangleClrIdentifier(original) == original) return;
        builder.SetCustomAttribute(new CustomAttributeBuilder(
            s_toshOriginalNameCtor, new object[] { original }));
    }
    private static void StampOriginalNameIfMangled(MethodBuilder builder, string original)
    {
        if (MangleClrIdentifier(original) == original) return;
        builder.SetCustomAttribute(new CustomAttributeBuilder(
            s_toshOriginalNameCtor, new object[] { original }));
    }

    private void FinalizeClrClassTypes()
    {
        // CLR requires that a base type's CreateType() is called before any
        // type that inherits from it. Perform a depth-first topological walk:
        // for each shell, recursively create its parent (if the parent is also
        // a shell in this compilation unit) before creating the shell itself.
        var created = new HashSet<string>(StringComparer.Ordinal);

        void CreateShell(ClrTypeShell shell)
        {
            if (!created.Add(shell.Name)) return;
            // If the parent TypeBuilder is also one of our shells, finalize it first.
            var parentType = shell.Type.BaseType;
            if (parentType is TypeBuilder parentBuilder
                && _clrShellsByType.TryGetValue(parentBuilder, out var parentShell))
            {
                CreateShell(parentShell);
            }
            shell.Type.CreateType();
        }

        foreach (var shell in _clrTypeShells.Values)
        {
            CreateShell(shell);
        }
    }

    private static bool TryResolveClrEnumUnderlyingType(string? typeName, out Type underlying)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            underlying = typeof(int);
            return true;
        }

        underlying = typeName.Trim().ToLowerInvariant() switch
        {
            "byte" or "system.byte" => typeof(byte),
            "sbyte" or "system.sbyte" => typeof(sbyte),
            "short" or "int16" or "system.int16" => typeof(short),
            "ushort" or "uint16" or "system.uint16" => typeof(ushort),
            "int" or "int32" or "system.int32" => typeof(int),
            "uint" or "uint32" or "system.uint32" => typeof(uint),
            "long" or "int64" or "system.int64" => typeof(long),
            "ulong" or "uint64" or "system.uint64" => typeof(ulong),
            _ => typeof(void),
        };

        return underlying != typeof(void);
    }

    private static bool TryBuildClrEnumLiteralValues(
        BoundEnumDefinition en,
        Type underlying,
        out object[] values)
    {
        values = new object[en.Members.Count];
        decimal nextValue = 0m;

        for (var i = 0; i < en.Members.Count; i++)
        {
            var member = en.Members[i];
            object? rawValue;
            if (member.Value is null)
            {
                rawValue = nextValue;
            }
            else
            {
                if (!TryGetLiteralDefaultValue(member.Value, out rawValue))
                    return false;
            }

            if (!TryConvertClrEnumLiteralValue(rawValue, underlying, out var converted))
                return false;

            values[i] = converted;
            try
            {
                nextValue = Convert.ToDecimal(converted, System.Globalization.CultureInfo.InvariantCulture) + 1m;
            }
            catch
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryConvertClrEnumLiteralValue(object? rawValue, Type underlying, out object value)
    {
        value = null!;
        if (!TryGetIntegralConstant(rawValue, out var numericValue))
            return false;

        try
        {
            value =
                underlying == typeof(byte) ? checked((byte)numericValue) :
                underlying == typeof(sbyte) ? checked((sbyte)numericValue) :
                underlying == typeof(short) ? checked((short)numericValue) :
                underlying == typeof(ushort) ? checked((ushort)numericValue) :
                underlying == typeof(int) ? checked((int)numericValue) :
                underlying == typeof(uint) ? checked((uint)numericValue) :
                underlying == typeof(long) ? checked((long)numericValue) :
                underlying == typeof(ulong) ? checked((ulong)numericValue) :
                null!;
            return value is not null;
        }
        catch
        {
            value = null!;
            return false;
        }
    }

    private static bool TryGetIntegralConstant(object? rawValue, out decimal value)
    {
        value = 0m;
        try
        {
            value = rawValue switch
            {
                byte v => v,
                sbyte v => v,
                short v => v,
                ushort v => v,
                int v => v,
                uint v => v,
                long v => v,
                ulong v => v,
                float v when !float.IsNaN(v) && !float.IsInfinity(v) => (decimal)v,
                double v when !double.IsNaN(v) && !double.IsInfinity(v) => (decimal)v,
                decimal v => v,
                _ => 0m,
            };
        }
        catch
        {
            return false;
        }

        if (rawValue is not byte and not sbyte and not short and not ushort
            and not int and not uint and not long and not ulong
            and not float and not double and not decimal)
        {
            return false;
        }

        return value == decimal.Truncate(value);
    }

    /// <summary>
    /// Walks the bound tree once looking for any
    /// <see cref="BoundBlockExpression"/>. Used to decide whether to
    /// emit the source-registration prologue.
    /// </summary>
    /// <summary>
    /// Extends a type-definition span forward to include any trailing
    /// brace or paren the parser left out. Tosh's parser sometimes
    /// reports a class/record/struct span that ends just before its
    /// closing <c>}</c> or <c>)</c>; the engine needs the full balanced
    /// source to re-parse. Walks forward counting brace/paren nesting
    /// (starting from the slice's own running count) until the
    /// outermost closer is consumed.
    /// </summary>
    private (int Start, int Length) ExtendTypeDefinitionSpan(TextSpan span)
    {
        var src = ((ParseResult)_unit.ParseResult).SourceText;
        var sliceEnd = span.Start + span.Length;

        // Compute running brace/paren depth across the original slice
        int braceDepth = 0;
        int parenDepth = 0;
        for (int i = span.Start; i < sliceEnd && i < src.Length; i++)
        {
            char ch = src[i];
            if (ch == '{') braceDepth++;
            else if (ch == '}') braceDepth--;
            else if (ch == '(') parenDepth++;
            else if (ch == ')') parenDepth--;
        }
        if (braceDepth <= 0 && parenDepth <= 0) return (span.Start, span.Length);

        int probe = sliceEnd;
        while (probe < src.Length && (braceDepth > 0 || parenDepth > 0))
        {
            char ch = src[probe];
            if (ch == '{') braceDepth++;
            else if (ch == '}') braceDepth--;
            else if (ch == '(') parenDepth++;
            else if (ch == ')') parenDepth--;
            probe++;
        }
        return (span.Start, probe - span.Start);
    }

}
