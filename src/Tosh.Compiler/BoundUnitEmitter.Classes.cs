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
    private void EnsureBaseClassShellDeclared(string baseName)
    {
        if (_clrTypeShells.ContainsKey(baseName)) return;
        foreach (var stmt in _unit.Root.Statements)
        {
            if (stmt is BoundClassDefinition baseCls
                && string.Equals(baseCls.Name, baseName, StringComparison.Ordinal)
                && CanEmitClrClassShell(baseCls))
            {
                DeclareClrClassShell(baseCls);
                return;
            }
        }
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

        // Resolve the parent TypeBuilder. If the base class is declared
        // in the same unit we recursively ensure its shell is declared
        // first, then use its TypeBuilder as the CLR parent. Unknown
        // base classes (external assemblies, not-yet-modeled shapes)
        // fall back to `object` — the shell is still reflectable even
        // if the inheritance chain is truncated at the CLR level.
        Type parentType = MetadataType(typeof(object));
        ClrTypeShell? baseShell = null;
        if (cls.BaseClassName is not null)
        {
            EnsureBaseClassShellDeclared(cls.BaseClassName);
            if (_clrTypeShells.TryGetValue(cls.BaseClassName, out baseShell))
                parentType = baseShell.Type;
        }

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

        // Locate the (at most one) explicit user-declared constructor.
        // When the class header has no primary-ctor parameters, the
        // explicit ctor's parameters drive the shell ctor signature
        // (e.g. `class Greeter { Greeter(name: string) { ... } }`).
        // When both forms exist, the primary ctor wins for the
        // signature and the explicit body still gets lowered into
        // the shell ctor IL after field copies.
        BoundClassConstructorMember? explicitCtor = null;
        foreach (var member in cls.Members)
        {
            if (member is BoundClassConstructorMember c) { explicitCtor = c; break; }
        }
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
        // Inheritance, abstract base, interfaces, and traits aren't
        // representable on a flat shell yet for direct construction.
        // Traits may carry property default values that are set by the
        // tosh evaluator during ToshHost.CreateObject — bypassing that
        // path with a bare newobj would silently drop those defaults.
        if (cls.BaseClassName is not null
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
        // Call the base constructor. When a base shell is known in this unit
        // we call its ctor directly, passing nulls for each typed parameter
        // slot so the IL is verifiable. When no base shell is known (external
        // type or unmodeled base) we fall back to object..ctor().
        ctorIl.Emit(OpCodes.Ldarg_0);
        if (baseShell is not null)
        {
            if (cls.BaseConstructorArgs is { Count: > 0 }
                && cls.BaseConstructorArgs.Count == baseShell.CtorParamTypes.Length)
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
                            ctorIl.Emit(OpCodes.Ldnull);
                        }
                        else
                        {
                            BoxIfValueType(baseArgType);
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
            else
            {
                for (var i = 0; i < baseShell.CtorParamTypes.Length; i++)
                    ctorIl.Emit(OpCodes.Ldnull);
            }
            ctorIl.Emit(OpCodes.Call, baseShell.Ctor);
        }
        else
        {
            ctorIl.Emit(OpCodes.Call, MetadataType(typeof(object)).GetConstructor(Type.EmptyTypes)!);
        }
        for (var i = 0; i < ctorSigParams.Count; i++)
        {
            var pname = ctorSigParams[i].Name;
            if (!fields.TryGetValue(pname, out var fb)) continue;
            ctorIl.Emit(OpCodes.Ldarg_0);
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
                    _paramSlots[ctorSigParams[i].Symbol] = i + 1;
                }

                foreach (var prop in cls.Members.OfType<BoundClassPropertyMember>())
                {
                    if (prop.Initializer is null) continue;
                    if (!fields.TryGetValue(prop.Name, out var fb)) continue;

                    ctorIl.Emit(OpCodes.Ldarg_0);
                    if (TryResolveCtorInitializerParameterSlot(prop.Initializer, ctorSigParams, out var paramSlot))
                    {
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
        if (explicitCtor is not null)
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
                // When the primary ctor drives the signature but the
                // explicit ctor's parameters need to be visible to
                // its body, they don't have arg slots. In that case
                // we currently don't support body lowering — guarded
                // by ctorSigParams == explicitCtor.Parameters above.
                if (ReferenceEquals(ctorSigParams, explicitCtor.Parameters))
                {
                    for (var i = 0; i < explicitCtor.Parameters.Count; i++)
                    {
                        _paramSlots[explicitCtor.Parameters[i].Symbol] = i + 1;
                    }
                    foreach (var stmt in explicitCtor.Body.Statements)
                    {
                        EmitStatement(stmt);
                    }
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
            }
        }
        ctorIl.Emit(OpCodes.Ret);

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
                        MangleClrIdentifier(m.Method.Name),
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
                    MangleClrIdentifier(m.Method.Name),
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
                // Computed properties (with getter/setter bodies)
                // and lazy props aren't lowered onto the shell yet —
                // routing such instances through host dispatch keeps
                // the engine's evaluator owning that behavior.
                if (prop.GetterBody is not null || prop.SetterBody is not null || prop.IsLazy)
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
            if (p.IsRest || p.IsOptional) return false;
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
            try
            {
                _il = pending.Method.GetILGenerator();
                _locals = new();
                _paramSlots = new();
                _typedParamLocals = new();
                _currentFunctionReturnType = null;       // object-typed return
                var isStaticMethod = pending.Method.IsStatic;
                _currentThisType = isStaticMethod ? null : pending.Shell.Type;
                // Instance methods: declared params start at slot 1
                // because arg 0 is `this`. Static methods start at 0.
                var argBase = isStaticMethod ? 0 : 1;
                for (var i = 0; i < pending.Definition.Parameters.Count; i++)
                {
                    _paramSlots[pending.Definition.Parameters[i].Symbol] = i + argBase;
                }
                foreach (var stmt in pending.Definition.Body.Statements)
                {
                    EmitStatement(stmt);
                }
                // Fall-through: implicit `return null`.
                _il.Emit(OpCodes.Ldnull);
                _il.Emit(OpCodes.Ret);
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
