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
    private void DeclareClrShellsInsideModule(BoundModuleDefinition mod)
    {
        foreach (var stmt in mod.Body.Statements)
        {
            switch (stmt)
            {
                case BoundClassDefinition cls when CanEmitClrClassShell(cls)
                    && !_clrTypeShells.ContainsKey(cls.Name):
                    DeclareClrClassShell(cls);
                    break;
                case BoundModuleDefinition nested:
                    DeclareClrShellsInsideModule(nested);
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
        foreach (var p in fn.Parameters)
        {
            if (p.IsRest || p.IsOptional || p.Default is not null) return false;
        }
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
        var paramTypes = new Type[fn.Parameters.Count];
        for (var i = 0; i < paramTypes.Length; i++) paramTypes[i] = MetadataType(typeof(object));
        var method = shell.Type.DefineMethod(
            MangleClrIdentifier(fn.Name),
            MethodAttributes.Public | MethodAttributes.Static,
            MetadataType(typeof(object)),
            paramTypes);
        StampOriginalNameIfMangled(method, fn.Name);
        for (var i = 0; i < fn.Parameters.Count; i++)
        {
            method.DefineParameter(i + 1, ParameterAttributes.None, fn.Parameters[i].Name);
        }
        _clrModuleMethodBodies.Add(new ClrModuleMethodPending(shell, method, fn));
    }

    private void EmitClrModuleMethodBodies()
    {
        foreach (var pending in _clrModuleMethodBodies)
        {
            var savedIl = _il;
            var savedLocals = _locals;
            var savedParams = _paramSlots;
            try
            {
                _il = pending.Method.GetILGenerator();
                _locals = new();
                _paramSlots = new();
                for (var i = 0; i < pending.Definition.Parameters.Count; i++)
                {
                    _paramSlots[pending.Definition.Parameters[i].Symbol] = i;
                }
                foreach (var stmt in pending.Definition.Body.Statements)
                {
                    EmitStatement(stmt);
                }
                // Implicit `return null` for fall-through.
                _il.Emit(OpCodes.Ldnull);
                _il.Emit(OpCodes.Ret);
            }
            finally
            {
                _il = savedIl;
                _locals = savedLocals;
                _paramSlots = savedParams;
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
