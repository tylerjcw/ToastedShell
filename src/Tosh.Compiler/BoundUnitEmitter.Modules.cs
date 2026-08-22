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
    /// <summary>
    /// Declare CLR shells for any "simple" type declarations that
    /// live inside a module body. Currently scoped to classes —
    /// records, structs, traits, etc. inside modules will follow in
    /// later steps. The class becomes a top-level CLR type
    /// (`<see cref="_assemblyName"/>.<see cref="BoundClassDefinition.Name"/>`)
    /// stamped with its tosh-original name; the engine still owns
    /// qualified-access semantics through the existing module source
    /// registration. This is what lifts module-nested classes out of
    /// Tier-3 source replay.
    /// </summary>
    private void DeclareClrShellsInsideModule(BoundModuleDefinition mod, string? qualifier = null)
    {
        var moduleQualifier = qualifier is null ? mod.Name : $"{qualifier}.{mod.Name}";

        foreach (var stmt in mod.Body.Statements)
        {
            switch (stmt)
            {
                case BoundClassDefinition cls when CanEmitClrClassShell(cls)
                    && !_clrTypeShells.ContainsKey(cls.Name):
                    DeclareClrClassShell(cls);
                    StampModuleQualifiedName(cls.Name, moduleQualifier);
                    break;

                // `TOAST-0035`, step 2. An interface joins the class, verified by running
                // it rather than by observing that it emits. `record`, `struct`, `trait` and
                // `union` each need more than the stamp and are left replaying — see the
                // item for what each one reported.
                case BoundInterfaceDefinition iface when !_clrTypeShells.ContainsKey(iface.Name):
                    DeclareClrInterfaceShell(iface);
                    StampModuleQualifiedName(iface.Name, moduleQualifier);
                    break;

                case BoundModuleDefinition nested:
                    DeclareClrShellsInsideModule(nested, moduleQualifier);
                    break;
            }
        }
    }

    /// <summary>
    /// True if a module body contains any declaration that the CLR
    /// shell can't represent natively (classes, records, structs,
    /// unions, enums, traits, side-effectful top-level statements,
    /// or funcs with unsupported parameter shapes / captures). Such
    /// modules still get a <see cref="TypeBuilder"/> shell, but the
    /// body is also re-evaluated via source replay at runtime — and
    /// that replay is the part the <c>runtime</c> /<c>pure</c>
    /// profiles need to reject.
    /// </summary>
    /// <summary>
    /// Records a module-nested shell under the name tosh calls it — <c>TOAST-0035</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A shell for a type declared inside a module is emitted as a top-level CLR type under
    /// its **bare** name, and `ToshHost.RegisterCompiledAssembly` aliases it by whatever
    /// <c>ToshOriginalNameAttribute</c> says, falling back to the CLR name. So `class Box`
    /// inside `module M` was registered as `Box`, and the emitted program could not find
    /// `M.Box`: *"unknown type 'M.Box' in `new` expression"*.
    /// </para>
    /// <para>
    /// That is why a declaration kind fails once its module stops being replayed, and it
    /// applied to classes too — which <see cref="ModuleNeedsSourceReplay"/> has accepted
    /// since "step 1", so the defect was already shipping rather than introduced by lifting
    /// more kinds out.
    /// </para>
    /// <para>
    /// Unconditional, unlike `StampOriginalNameIfMangled`, which stamps only a name the CLR
    /// could not spell. A qualified name is never what the CLR type is called, so there is
    /// nothing to compare it against.
    /// </para>
    /// </remarks>
    private void StampModuleQualifiedName(string declaredName, string moduleQualifier)
    {
        if (!_clrTypeShells.TryGetValue(declaredName, out var shell))
        {
            return;
        }

        shell.Type.SetCustomAttribute(new CustomAttributeBuilder(
            s_toshOriginalNameCtor,
            new object[] { $"{moduleQualifier}.{declaredName}" }));
    }

    private bool ModuleNeedsSourceReplay(BoundModuleDefinition mod)
    {
        foreach (var stmt in mod.Body.Statements)
        {
            switch (stmt)
            {
                case BoundVariableDeclaration:
                    continue;
                case BoundFunctionDefinition fn when CanEmitClrModuleMethod(fn):
                    continue;
                case BoundClassDefinition cls when CanEmitClrClassShell(cls):
                    // Step 1 of the first-class .NET plan: simple class
                    // declarations inside a module are emittable as real
                    // CLR shells (top-level types stamped with the
                    // module-qualified original name). They no longer
                    // force the enclosing module body into Tier-3 replay.
                    continue;
                // `TOAST-0035`, step 2. Accepted only where a shell is produced, stamped,
                // *and* the emitted program was run and gave the interpreted answer. The
                // first attempt accepted six kinds on the strength of emitting, and all six
                // failed at run time.
                case BoundInterfaceDefinition:
                    continue;

                case BoundModuleDefinition nested when !ModuleNeedsSourceReplay(nested):
                    continue;
                default:
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Declare a real CLR static-class shell for one module
    /// definition. Top-level modules become top-level types; nested
    /// modules become nested types under their parent. Multiple
    /// <c>partial module</c> declarations sharing the same qualified
    /// name reuse the same <see cref="TypeBuilder"/>.
    /// </summary>
    private void DeclareClrModuleShell(BoundModuleDefinition mod, ClrModuleShell? parent, string qualifiedName)
    {
        if (!_clrModules.TryGetValue(qualifiedName, out var shell))
        {
            const TypeAttributes baseAttrs =
                TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.Abstract;
            TypeBuilder typeBuilder;
            if (parent is null)
            {
                typeBuilder = _moduleBuilder.DefineType(
                    $"{_assemblyName}.{MangleClrIdentifier(mod.Name)}",
                    TypeAttributes.Public | baseAttrs,
                    MetadataType(typeof(object)));
            }
            else
            {
                typeBuilder = parent.Type.DefineNestedType(
                    MangleClrIdentifier(mod.Name),
                    TypeAttributes.NestedPublic | baseAttrs,
                    MetadataType(typeof(object)));
            }
            StampOriginalNameIfMangled(typeBuilder, mod.Name);
            // Stamp the type with its qualified tosh module name so ToshHost
            // can build a qualifiedName → Type map from the compiled assembly
            // without relying on name-mangling correlation.
            var moduleShellAttrCtor = typeof(global::Tosh.Runtime.ToshModuleShellAttribute)
                .GetConstructor(new[] { typeof(string) })!;
            typeBuilder.SetCustomAttribute(new CustomAttributeBuilder(
                moduleShellAttrCtor, new object[] { qualifiedName }));
            shell = new ClrModuleShell(qualifiedName, typeBuilder, parent);
            _clrModules[qualifiedName] = shell;
            parent?.Nested.Add(shell);
        }

        // Walk the body twice so all module-scope fields are
        // registered before any method's capture validation runs.
        // Pass 1: vars (registers static fields). Pass 2: funcs and
        // nested modules. Anything else (classes, records, top-level
        // statements with side effects) stays unrepresented in the
        // CLR shell — source-replay handles those.
        foreach (var stmt in mod.Body.Statements)
        {
            if (stmt is BoundVariableDeclaration vd)
            {
                DeclareModuleField(shell, vd);
            }
        }
        foreach (var stmt in mod.Body.Statements)
        {
            switch (stmt)
            {
                case BoundFunctionDefinition fn when CanEmitClrModuleMethod(fn):
                    DeclareModuleMethod(shell, fn);
                    break;

                case BoundModuleDefinition nested:
                    DeclareClrModuleShell(nested, shell, $"{qualifiedName}.{nested.Name}");
                    break;
            }
        }
    }

    /// <summary>
    /// True if the function definition can be safely emitted as a
    /// real CLR static method on a module type. Every capture must
    /// resolve to either a peer top-level user function (dispatched
    /// through <see cref="_userFunctions"/>) or a symbol that has
    /// been promoted to a static field (top-level or module-scope).
    /// </summary>
    private bool CanEmitClrModuleMethod(BoundFunctionDefinition fn)
    {
        // `TOAST-0035`. An optional, rest, or defaulted parameter no longer forces the
        // enclosing module into source replay: the method is emitted taking its arguments
        // packed, exactly as a top-level function with the same shape already was. Refusing
        // them here is what replayed five of the sixteen library files measured, none of
        // which contained a single declaration the emitter could not handle.
        foreach (var capture in fn.Captures)
        {
            if (_staticFields.ContainsKey(capture)) continue;
            if (_topLevelFunctionNames.Contains(capture.Name)) continue;
            return false;
        }
        return true;
    }

    private void DeclareModuleField(ClrModuleShell shell, BoundVariableDeclaration vd)
    {
        if (shell.Fields.ContainsKey(vd.Symbol.Name)) return;
        if (_staticFields.ContainsKey(vd.Symbol)) return;

        var field = shell.Type.DefineField(
            MangleClrIdentifier(vd.Symbol.Name),
            MetadataType(typeof(object)),
            FieldAttributes.Public | FieldAttributes.Static);
        StampOriginalNameIfMangled(field, vd.Symbol.Name);
        shell.Fields[vd.Symbol.Name] = field;
        // Registering on _staticFields lets the standard
        // EmitVariableDeclaration / EmitVariableReference paths
        // emit `stsfld` / `ldsfld` against the right field token
        // automatically — no module-aware special-casing needed
        // in the expression emitter.
        _staticFields[vd.Symbol] = field;
        _clrModuleFieldInits.Add(new ClrModuleFieldPending(shell, vd));
    }

    private void DeclareModuleMethod(ClrModuleShell shell, BoundFunctionDefinition fn)
    {
        var packed = ModuleMethodUsesPackedArguments(fn);

        var paramTypes = packed
            ? new[] { MetadataType(typeof(object[])) }
            : new Type[fn.Parameters.Count];

        if (!packed)
        {
            for (var i = 0; i < paramTypes.Length; i++) paramTypes[i] = MetadataType(typeof(object));
        }

        var method = shell.Type.DefineMethod(
            MangleClrIdentifier(fn.Name),
            MethodAttributes.Public | MethodAttributes.Static,
            MetadataType(typeof(object)),
            paramTypes);
        StampOriginalNameIfMangled(method, fn.Name);

        if (packed)
        {
            method.DefineParameter(1, ParameterAttributes.None, "args");

            var packedAttrCtor = typeof(global::Tosh.Runtime.ToshPackedArgumentsAttribute)
                .GetConstructor(new[] { typeof(int) })!;
            method.SetCustomAttribute(
                new CustomAttributeBuilder(packedAttrCtor, new object[] { fn.Parameters.Count }));
        }
        else
        {
            for (var i = 0; i < fn.Parameters.Count; i++)
            {
                method.DefineParameter(i + 1, ParameterAttributes.None, fn.Parameters[i].Name);
            }
        }

        _clrModuleMethodBodies.Add(new ClrModuleMethodPending(shell, method, fn));
    }

    /// <summary>
    /// Whether a module method must take its arguments packed — <c>TOAST-0035</c>.
    /// </summary>
    /// <remarks>
    /// The same condition top-level functions use. A default may be any expression, so its
    /// value has to be produced inside the body, which means the body has to be able to tell
    /// "omitted" from "passed null" — and that needs the array.
    /// </remarks>
    private static bool ModuleMethodUsesPackedArguments(BoundFunctionDefinition fn)
    {
        foreach (var p in fn.Parameters)
        {
            if (p.IsRest || p.IsOptional || p.Default is not null) return true;
        }

        return false;
    }

    private void EmitClrModuleMethodBodies()
    {
        foreach (var pending in _clrModuleMethodBodies)
        {
            var savedIl = _il;
            var savedLocals = _locals;
            var savedParams = _paramSlots;
            var savedReturnEmissionFrame = _returnEmissionFrame;
            var savedDeferredCleanupFrames = _deferredCleanupFrames;
            // `TOAST-0035`. The packed prologue binds parameters into this map; without
            // saving it, one module method's locals would still be visible while emitting
            // the next, which is a wrong-IL bug rather than a failing one.
            var savedTypedParamLocals = new Dictionary<BoundSymbol, LocalBuilder>(_typedParamLocals);
            try
            {
                _il = pending.Method.GetILGenerator();
                _locals = new();
                _paramSlots = new();
                _deferredCleanupFrames = new();
                var returnFrame = CreateReturnEmissionFrame(typeof(object));
                _returnEmissionFrame = returnFrame;
                var executionFrame = EmitExecutionFrameEntry($"module {pending.Module.QualifiedName}.{pending.Definition.Name}");

                if (ModuleMethodUsesPackedArguments(pending.Definition))
                {
                    // `TOAST-0035`. The same prologue a top-level function uses, which is
                    // why it was lifted out of `EmitUserFunctionBody` rather than copied.
                    EmitPackedArgumentPrologue(pending.Definition.Parameters);
                }
                else
                {
                    for (var i = 0; i < pending.Definition.Parameters.Count; i++)
                    {
                        _paramSlots[pending.Definition.Parameters[i].Symbol] = i;
                    }
                }
                // `TOAST-0035`. The same collapse a top-level function and a class method
                // both use. Without it a module method with an expression body — `func
                // Add(a, b) -> int => $a + $b`, which is most of a library — computed its
                // value, discarded it, and fell through to the implicit `return null`
                // below. It emitted cleanly and returned nothing, on both profiles.
                EmitBlock(CollapseTrailingExpressionIntoReturn(pending.Definition));

                // Implicit `return null` for fall-through.
                _il.Emit(OpCodes.Ldnull);
                _il.Emit(OpCodes.Stloc, returnFrame.ValueLocal!);
                EmitExecutionFrameExit(executionFrame);
                EmitReturnEpilogue(returnFrame);
            }
            finally
            {
                _typedParamLocals.Clear();
                foreach (var (symbol, local) in savedTypedParamLocals) _typedParamLocals[symbol] = local;
                _il = savedIl;
                _locals = savedLocals;
                _paramSlots = savedParams;
                _returnEmissionFrame = savedReturnEmissionFrame;
                _deferredCleanupFrames = savedDeferredCleanupFrames;
            }
        }
    }

    private void FinalizeClrModuleCctors()
    {
        // Emit each pending var initializer into its owning module's
        // .cctor. The field is already registered in `_staticFields`,
        // so EmitVariableDeclaration takes the static-field path
        // (stsfld) automatically.
        foreach (var pending in _clrModuleFieldInits)
        {
            var shell = pending.Module;
            if (shell.Cctor is null)
            {
                shell.Cctor = shell.Type.DefineTypeInitializer();
                shell.CctorIl = shell.Cctor.GetILGenerator();
            }
            var savedIl = _il;
            var savedLocals = _locals;
            var savedParams = _paramSlots;
            try
            {
                _il = shell.CctorIl!;
                _locals = new();
                _paramSlots = new();
                EmitVariableDeclaration(pending.Declaration);
            }
            finally
            {
                _il = savedIl;
                _locals = savedLocals;
                _paramSlots = savedParams;
            }
        }

        foreach (var shell in _clrModules.Values)
        {
            if (shell.CctorIl is { } cctorIl)
            {
                cctorIl.Emit(OpCodes.Ret);
            }
        }
    }

    private void FinalizeClrModuleTypes()
    {
        // Nested types must be created before their declaring type.
        // Walk roots, recurse depth-first, then create on the way back.
        foreach (var shell in _clrModules.Values)
        {
            if (shell.Parent is null) CreateClrModuleType(shell);
        }
    }

    private static void CreateClrModuleType(ClrModuleShell shell)
    {
        foreach (var nested in shell.Nested)
        {
            CreateClrModuleType(nested);
        }
        shell.Type.CreateType();
    }

    /// <summary>
    /// True if <paramref name="cls"/> is "simple" enough for v1 CLR
    /// lowering: no base class, no interfaces, no traits, not
    /// abstract / partial, no custom constructors, every
    /// member is either a non-static / non-computed / non-lazy
    /// storage property or a method (methods are skipped from the
    /// shell), and every primary-ctor parameter is positional and
    /// non-rest. Failure to match means the type stays Tier 3
    /// (source-replay only) so external callers won't see a
    /// half-formed CLR shell that lacks members the tosh runtime
    /// uses.
    /// </summary>
    /// <summary>
    /// Emits a <see cref="global::Tosh.Runtime.ToshModuleAttribute"/>
    /// assembly attribute for <paramref name="module"/> and recurses
    /// into nested modules so each fully-qualified module path is
    /// recorded. <paramref name="parentPath"/> is the dotted prefix
    /// or <c>null</c> at the root.
    /// </summary>
    private void EmitToshModuleAttributes(BoundModuleDefinition module, string? parentPath)
    {
        var qualified = parentPath is null ? module.Name : $"{parentPath}.{module.Name}";
        var (start, length) = ExtendTypeDefinitionSpan(module.Span);
        var ctor = typeof(global::Tosh.Runtime.ToshModuleAttribute)
            .GetConstructor(new[] { typeof(string), typeof(int), typeof(int) })!;
        _ab.SetCustomAttribute(new CustomAttributeBuilder(
            ctor,
            new object[] { qualified, start, length }));

        foreach (var stmt in module.Body.Statements)
        {
            if (stmt is BoundModuleDefinition nested)
            {
                EmitToshModuleAttributes(nested, qualified);
            }
        }
    }
}
