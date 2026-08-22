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
    /// Pushes a boxed object literal onto the stack for use as the
    /// default-value argument in compiled subcommand param descriptors.
    /// </summary>
    private void EmitObjectLiteral(object? value)
    {
        switch (value)
        {
            case null:
                _il.Emit(OpCodes.Ldnull);
                break;
            case bool b:
                _il.Emit(b ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                _il.Emit(OpCodes.Box, typeof(bool));
                break;
            case int i:
                _il.Emit(OpCodes.Ldc_I4, i);
                _il.Emit(OpCodes.Box, typeof(int));
                break;
            case long l:
                _il.Emit(OpCodes.Ldc_I8, l);
                _il.Emit(OpCodes.Box, typeof(long));
                break;
            case double d:
                _il.Emit(OpCodes.Ldc_R8, d);
                _il.Emit(OpCodes.Box, typeof(double));
                break;
            case string s:
                _il.Emit(OpCodes.Ldstr, s);
                break;
            default:
                // Fallback: convert to string representation.
                _il.Emit(OpCodes.Ldstr, value.ToString() ?? "");
                break;
        }
    }


    /// <summary>
    /// Mangles a tosh identifier into a CLR-friendly identifier
    /// that downstream consumers (C#, F#, ILSpy, IDE tooling) can
    /// reference without escapes. Tosh allows hyphens in user-
    /// <summary>
    /// Walks <see cref="_unit"/>'s top-level statements and reports
    /// pairs of distinct tosh identifiers that mangle to the same
    /// CLR identifier within their respective namespace group
    /// (top-level types, top-level functions). Emits
    /// <c>tosh.compile.name_mangling_collision</c>-shaped diagnostics
    /// via <see cref="Diagnostics"/>.
    /// </summary>
    private void DetectNameManglingCollisions()
    {
        var typeBuckets = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var funcBuckets = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        static void Bucket(Dictionary<string, List<string>> map, string original)
        {
            var mangled = MangleClrIdentifier(original);
            if (!map.TryGetValue(mangled, out var list))
            {
                list = new List<string>();
                map[mangled] = list;
            }
            // Skip exact duplicates — those are real "duplicate
            // definition" errors reported elsewhere; a collision
            // diagnostic on top would just be noise.
            if (!list.Contains(original, StringComparer.Ordinal))
            {
                list.Add(original);
            }
        }

        foreach (var statement in _unit.Root.Statements)
        {
            switch (statement)
            {
                case BoundClassDefinition c: Bucket(typeBuckets, c.Name); break;
                case BoundRecordDefinition r: Bucket(typeBuckets, r.Name); break;
                case BoundStructDefinition s: Bucket(typeBuckets, s.Name); break;
                case BoundEnumDefinition e: Bucket(typeBuckets, e.Name); break;
                case BoundUnionDefinition u: Bucket(typeBuckets, u.Name); break;
                case BoundInterfaceDefinition i: Bucket(typeBuckets, i.Name); break;
                case BoundTraitDefinition t: Bucket(typeBuckets, t.Name); break;
                case BoundModuleDefinition m: Bucket(typeBuckets, m.Name); break;
                case BoundFunctionDefinition f: Bucket(funcBuckets, f.Name); break;
            }
        }

        foreach (var (mangled, originals) in typeBuckets)
        {
            if (originals.Count >= 2)
            {
                Diagnostics.Add(
                    $"tosh.compile.name_mangling_collision: top-level types " +
                    $"[{string.Join(", ", originals.Select(o => $"'{o}'"))}] " +
                    $"all mangle to CLR identifier '{mangled}'");
            }
        }
        foreach (var (mangled, originals) in funcBuckets)
        {
            if (originals.Count >= 2)
            {
                Diagnostics.Add(
                    $"tosh.compile.name_mangling_collision: top-level functions " +
                    $"[{string.Join(", ", originals.Select(o => $"'{o}'"))}] " +
                    $"all mangle to CLR identifier '{mangled}'");
            }
        }
    }

    /// <summary>
    /// Translates a tosh identifier into a valid CLR identifier so
    /// it can be used in <c>DefineMethod</c> / <c>DefineType</c> /
    /// <c>DefineField</c>. Tosh allows hyphens and other CLR-illegal
    /// characters in user-defined names (e.g. <c>func to-json</c>);
    /// the CLR accepts them at the metadata level but every C-style
    /// language rejects them. This translates each non-identifier
    /// character to <c>_</c> and prepends <c>_</c> when the original
    /// starts with a digit. Tosh names that are already valid CLR
    /// identifiers pass through unchanged.
    /// </summary>
    internal static string MangleClrIdentifier(string toshName)
    {
        if (string.IsNullOrEmpty(toshName)) return "_";
        var needsMangling = false;
        for (int i = 0; i < toshName.Length; i++)
        {
            var c = toshName[i];
            if (i == 0 && char.IsDigit(c)) { needsMangling = true; break; }
            if (!(char.IsLetterOrDigit(c) || c == '_')) { needsMangling = true; break; }
        }
        if (!needsMangling) return toshName;

        var sb = new System.Text.StringBuilder(toshName.Length + 1);
        if (char.IsDigit(toshName[0])) sb.Append('_');
        for (int i = 0; i < toshName.Length; i++)
        {
            var c = toshName[i];
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Maps a TōSh operator symbol used as a method name (e.g. <c>+</c>) to the
    /// CLR-canonical <c>op_*</c> name (<c>op_Addition</c>, etc.) so cross-language
    /// consumers can resolve the overload by name. Non-operator names pass
    /// through unchanged. Note: CLR operators are conventionally static and
    /// two-arg; TōSh emits these as instance methods with an implicit
    /// <c>$this</c>, so C# consumers see them as <c>obj.op_Addition(other)</c>
    /// rather than <c>obj + other</c>. Symbolic dispatch (=~, !~, **, //) has
    /// no CLR convention and keeps a raw <c>op_</c>-prefixed name.
    /// </summary>
    internal static string ToClrOperatorName(string toshMethodName) => toshMethodName switch
    {
        "+" => "op_Addition",
        "-" => "op_Subtraction",
        "*" => "op_Multiply",
        "/" => "op_Division",
        "%" => "op_Modulus",
        "==" => "op_Equality",
        "!=" => "op_Inequality",
        "<" => "op_LessThan",
        "<=" => "op_LessThanOrEqual",
        ">" => "op_GreaterThan",
        ">=" => "op_GreaterThanOrEqual",
        "**" => "op_ToshPower",
        "//" => "op_ToshIntegerDivision",
        "=~" => "op_ToshRegexMatch",
        "!~" => "op_ToshRegexNotMatch",
        _ => toshMethodName,
    };
    /// <see cref="global::Tosh.Compiler.Runtime.ToshHost.RegisterTypeFromSource"/>.
    /// Returns the source span that should be re-evaluated.
    /// </summary>
    private static bool IsTypeDefinitionStatement(BoundStatement stmt, out TextSpan span)
    {
        switch (stmt)
        {
            case BoundClassDefinition c: span = c.Span; return true;
            case BoundRecordDefinition r: span = r.Span; return true;
            case BoundStructDefinition s: span = s.Span; return true;
            case BoundEnumDefinition e: span = e.Span; return true;
            case BoundUnionDefinition u: span = u.Span; return true;
            case BoundInterfaceDefinition i: span = i.Span; return true;
            case BoundTraitDefinition t: span = t.Span; return true;
            case BoundTypeAliasStatement ta: span = ta.Span; return true;
            case BoundEventDefinition ev: span = ev.Span; return true;
            default: span = default; return false;
        }
    }

    /// <summary>
    /// Walks every <see cref="BoundFunctionDefinition"/> in the unit
    /// (top-level and nested) collecting captured symbols, then
    /// promotes those that refer to top-level variables to static
    /// fields on the program type. Top-level function names are
    /// also indexed so capture references that resolve to a peer
    /// user function can be ignored at IL time.
    /// </summary>
    private void PromoteCapturedSymbols()
    {
        // Index: top-level function names. The user-facing name
        // matches the capture symbol's Name field.
        foreach (var stmt in _unit.Root.Statements)
        {
            if (stmt is BoundFunctionDefinition fn)
            {
                _topLevelFunctionNames.Add(fn.Name);
            }
        }

        // Set: top-level variable symbols. A capture is eligible
        // for static-field promotion only when its target symbol
        // matches one of these (or one of the top-level function
        // symbols, which we resolve through _userFunctions instead).
        var topLevelSymbols = new HashSet<BoundSymbol>();
        foreach (var stmt in _unit.Root.Statements)
        {
            if (stmt is BoundVariableDeclaration decl)
            {
                topLevelSymbols.Add(decl.Symbol);
            }
        }

        // Collect every capture from every function definition in
        // the unit (recursively, since nested funcs may declare
        // their own).
        var seen = new HashSet<BoundSymbol>();
        var ordered = new List<BoundSymbol>();
        CollectCaptures(_unit.Root, seen, ordered);

        foreach (var sym in ordered)
        {
            if (!topLevelSymbols.Contains(sym)) continue;
            // Already promoted? (Defensive — `seen` should prevent
            // duplicates, but the same outer symbol can legitimately
            // appear once.)
            if (_staticFields.ContainsKey(sym)) continue;
            var field = _program.DefineField(
                $"_capture_{sym.Name}_{_staticFields.Count}",
                MetadataType(typeof(object)),
                FieldAttributes.Private | FieldAttributes.Static);
            _staticFields[sym] = field;
        }

        static void CollectCaptures(BoundNode? node, HashSet<BoundSymbol> seen, List<BoundSymbol> ordered)
        {
            if (node is null) return;
            if (node is BoundFunctionDefinition fn)
            {
                foreach (var c in fn.Captures)
                {
                    if (seen.Add(c)) ordered.Add(c);
                }
            }
            var type = node.GetType();
            foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (prop.GetIndexParameters().Length != 0) continue;
                object? value;
                try { value = prop.GetValue(node); }
                catch { continue; }
                if (value is null) continue;
                if (value is BoundNode child)
                {
                    CollectCaptures(child, seen, ordered);
                }
                else if (value is System.Collections.IEnumerable seq && value is not string)
                {
                    foreach (var item in seq)
                    {
                        if (item is BoundNode bn) CollectCaptures(bn, seen, ordered);
                    }
                }
            }
        }
    }

    private void DeclareUserFunction(BoundFunctionDefinition func)
    {
        // Captured outer variables are supported when every capture
        // either resolves to a top-level symbol promoted into a
        // static field (see PromoteCapturedSymbols) or names another
        // top-level function (those are dispatched through the
        // _userFunctions map, not via a value slot).
        foreach (var capture in func.Captures)
        {
            if (_staticFields.ContainsKey(capture)) continue;
            if (_topLevelFunctionNames.Contains(capture.Name)) continue;
            Diagnostics.Add(
                $"function '{func.Name}' captures '{capture.Name}' from a non-top-level scope (nested closures unsupported)");
            return;
        }
        // Obtain (or create) the overload list for this name.
        if (!_userFunctions.TryGetValue(func.Name, out var overloadList))
        {
            overloadList = new List<UserFunction>();
            _userFunctions[func.Name] = overloadList;
        }
        var overloadIndex = overloadList.Count;
        var totalOverloads = _topLevelFunctionOverloadCounts?.GetValueOrDefault(func.Name, 1) ?? 1;
        var usesPackedArguments = false;
        foreach (var p in func.Parameters)
        {
            if (p.IsRest || p.IsOptional || p.Default is not null)
            {
                usesPackedArguments = true;
                break;
            }
        }

        // Resolve declared CLR types per parameter and return slot.
        // Concrete BoundType.ClrType wins; everything else falls back
        // to <c>object</c> so the legacy dynamic dispatch shape is
        // preserved for unannotated and `: dynamic` slots.
        var paramClrTypes = new Type[func.Parameters.Count];
        var allParamsTyped = true;
        for (var i = 0; i < func.Parameters.Count; i++)
        {
            var declared = func.Parameters[i].Symbol.DeclaredType;
            var clr = declared is { IsConcrete: true } ? declared.ClrType : null;
            if (clr is null || clr == typeof(object))
            {
                paramClrTypes[i] = typeof(object);
                allParamsTyped = false;
            }
            else
            {
                paramClrTypes[i] = clr;
            }
        }
        var returnBound = func.ReturnType;
        var returnClr = returnBound is { IsConcrete: true } ? returnBound.ClrType ?? typeof(object) : typeof(object);
        // A function is "fully typed" only when EVERY parameter
        // carries a concrete annotation AND the return is concrete
        // and non-object. Mixed shapes stay on the dynamic path —
        // matches what CheckCompileAnnotations enforces in compile
        // mode while keeping fully-untyped scripts (BoundUnitEmitter
        // is also exercised directly by tests with `func get() { …
        // }` style, no annotations) on their existing IL shape.
        var isTyped = allParamsTyped && returnClr != typeof(object) && func.Parameters.Count > 0
            || (func.Parameters.Count == 0 && returnClr != typeof(object));
        // Packed-argument functions currently run through the dynamic
        // body path so optional/default/rest binding can happen in IL
        // before parameter locals are read.
        if (usesPackedArguments) isTyped = false;
        // Avoid name collisions with the auto-generated `Main`.
        if (string.Equals(func.Name, "Main", StringComparison.Ordinal)) isTyped = false;

        // CLR method naming for overloads. Goal: drop the legacy
        // `__ov{index}` suffix and emit overloads as same-name CLR
        // methods with distinct signatures, so ToastScript-built
        // libraries are indistinguishable from C# libraries to
        // consumers (Roslyn, F#, reflection-driven tooling). We
        // suffix only when an overload's CLR signature would
        // collide with one already claimed for the same name.
        // Same-signature overloads are unreachable anyway — the
        // binder leaves their call sites unresolved — so the
        // fallback name is purely a defensive sigil.
        var mangledBase = MangleClrIdentifier(func.Name);
        var sigKey = BuildOverloadSignatureKey(
            isTyped, paramClrTypes, returnClr, usesPackedArguments, func.Parameters.Count);
        if (!_seenOverloadSignatures.TryGetValue(func.Name, out var claimed))
        {
            claimed = new HashSet<string>(StringComparer.Ordinal);
            _seenOverloadSignatures[func.Name] = claimed;
        }
        var collides = !claimed.Add(sigKey);
        var clrMethodName = collides
            ? (isTyped ? $"{mangledBase}__ov{overloadIndex}" : $"Func_{mangledBase}__ov{overloadIndex}")
            : (isTyped ? mangledBase : $"Func_{mangledBase}");

        MethodBuilder primary;
        if (isTyped)
        {
            primary = _program.DefineMethod(
                clrMethodName,
                MethodAttributes.Public | MethodAttributes.Static,
                MetadataType(returnClr),
                MetadataTypes(paramClrTypes));
            StampOriginalNameIfMangled(primary, func.Name);
            for (var i = 0; i < func.Parameters.Count; i++)
            {
                StampTypedParameterAbi(primary, i, func.Parameters[i], paramClrTypes[i]);
            }
        }
        else
        {
            var shimParamTypes = usesPackedArguments
                ? [MetadataType(typeof(object[]))]
                : new Type[func.Parameters.Count];
            if (!usesPackedArguments)
            {
                for (var i = 0; i < shimParamTypes.Length; i++) shimParamTypes[i] = MetadataType(typeof(object));
            }
            primary = _program.DefineMethod(
                clrMethodName,
                MethodAttributes.Public | MethodAttributes.Static,
                MetadataType(typeof(object)),
                shimParamTypes);
            StampOriginalNameIfMangled(primary, func.Name);
            if (usesPackedArguments)
            {
                primary.DefineParameter(1, ParameterAttributes.None, "args");
            }
            else
            {
                for (var i = 0; i < func.Parameters.Count; i++)
                {
                    primary.DefineParameter(i + 1, ParameterAttributes.None, func.Parameters[i].Name);
                }
            }
        }

        overloadList.Add(new UserFunction(
            primary,
            func,
            isTyped,
            usesPackedArguments,
            paramClrTypes,
            isTyped ? returnClr : typeof(object)));
    }

    /// <summary>
    /// Stack transition: <c>object → target</c>. Routes through the same
    /// annotation conversion bridge as the interpreter so shell-native types
    /// (including quantities) and primitives obey one boundary contract.
    /// </summary>
    private void CoerceObjectToTyped(
        ILGenerator il,
        Type target,
        string? annotationTypeName = null,
        TextSpan? span = null,
        string? owner = null)
    {
        if (target == typeof(object)) return;

        // The pure profile promises an artifact carrying no reference to
        // `Tosh.Compiler.Runtime` (`TS-P1-25`), and `ToshHost.CheckType` needs the whole
        // engine — it delegates to `ConvertValueToAnnotatedType`, so there is no runtime
        // primitive to fall back to the way `EmitEnterExecutionFrameCall` has one. The
        // corelib conversion this replaced is kept for that profile instead.
        //
        // Nothing is lost by the split: routing through the annotation boundary exists to
        // give shell-native types (quantities above all) one conversion contract, and a pure
        // artifact cannot hold one — those shapes are rejected as tier violations before
        // emission. `func add(a: int, b: int) -> int` is the case that matters here, and
        // `Convert.ChangeType` answers it identically.
        if (_profile == CompileProfile.Pure)
        {
            if (target.IsValueType)
            {
                il.Emit(OpCodes.Ldtoken, target);
                il.Emit(OpCodes.Call, typeof(Type).GetMethod(
                    nameof(Type.GetTypeFromHandle), new[] { typeof(RuntimeTypeHandle) })!);
                il.Emit(OpCodes.Call, typeof(System.Convert).GetMethod(
                    nameof(System.Convert.ChangeType), new[] { typeof(object), typeof(Type) })!);
                il.Emit(OpCodes.Unbox_Any, target);
                return;
            }

            il.Emit(OpCodes.Castclass, target);
            return;
        }

        il.Emit(OpCodes.Ldstr, annotationTypeName ?? target.FullName ?? target.Name);
        il.Emit(OpCodes.Ldc_I4, span?.Start ?? 0);
        il.Emit(OpCodes.Ldc_I4, span?.Length ?? 0);
        il.Emit(OpCodes.Ldstr, owner ?? "compiled typed boundary");
        il.Emit(OpCodes.Call, s_hostCheckType);

        if (target.IsValueType)
        {
            il.Emit(OpCodes.Unbox_Any, target);
            return;
        }
        il.Emit(OpCodes.Castclass, target);
    }

    private void EmitUserFunctionBody(BoundFunctionDefinition func)
    {
        if (!_userFunctions.TryGetValue(func.Name, out var entries)) return;
        UserFunction entry = default;
        var entryFound = false;
        foreach (var e in entries)
        {
            if (e.Definition == func) { entry = e; entryFound = true; break; }
        }
        if (!entryFound)
        {
            // Declaration was rejected (closure / bad params).
            return;
        }

        var savedIl = _il;
        var savedLocals = _locals;
        var savedParams = _paramSlots;
        var savedTypedLocals = _typedParamLocals;
        var savedReturnType = _currentFunctionReturnType;
        var savedReturnTypeName = _currentFunctionReturnTypeName;
        var savedReturnRefinement = _currentFunctionReturnRefinement;
        var savedReturnEmissionFrame = _returnEmissionFrame;
        var savedDeferredCleanupFrames = _deferredCleanupFrames;
        try
        {
            _il = entry.Method.GetILGenerator();
            _locals = new();
            _paramSlots = new();
            _typedParamLocals = new();
            _currentFunctionReturnType = entry.IsTyped ? entry.ReturnClrType : null;
            _currentFunctionReturnTypeName = entry.IsTyped ? func.ReturnTypeName : null;
            _currentFunctionReturnRefinement = entry.IsTyped ? func.ReturnType as RefinementType : null;
            _deferredCleanupFrames = new();
            var returnFrame = CreateReturnEmissionFrame(entry.ReturnClrType);
            _returnEmissionFrame = returnFrame;
            var executionFrame = EmitExecutionFrameEntry($"func {func.Name}");
            if (entry.UsesPackedArguments)
            {
                // Resolve named-argument wrappers into their positional
                // slots before the positional prologue binds anything
                // (TS-P1-05): `f(1, c = 99)` must land 99 in c's slot,
                // leaving b's slot to its declared default. The result
                // lives in a local rather than overwriting arg 0, so the
                // prologue below reads one stable array.
                var hasRestParameter = func.Parameters.Count > 0 && func.Parameters[^1].IsRest;
                var argsLocal = _il.DeclareLocal(typeof(object[]));
                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Ldc_I4, func.Parameters.Count);
                _il.Emit(OpCodes.Newarr, typeof(string));
                for (var i = 0; i < func.Parameters.Count; i++)
                {
                    _il.Emit(OpCodes.Dup);
                    _il.Emit(OpCodes.Ldc_I4, i);
                    _il.Emit(OpCodes.Ldstr, func.Parameters[i].Name);
                    _il.Emit(OpCodes.Stelem_Ref);
                }
                _il.Emit(hasRestParameter ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                _il.Emit(OpCodes.Call, s_hostNormalizePackedArguments);
                _il.Emit(OpCodes.Stloc, argsLocal);

                for (var i = 0; i < func.Parameters.Count; i++)
                {
                    var parameter = func.Parameters[i];
                    var local = _il.DeclareLocal(typeof(object));

                    if (parameter.IsRest)
                    {
                        var restLocal = _il.DeclareLocal(s_listOfObject);
                        var idxLocal = _il.DeclareLocal(typeof(int));
                        var loop = _il.DefineLabel();
                        var done = _il.DefineLabel();

                        _il.Emit(OpCodes.Newobj, s_listCtor);
                        _il.Emit(OpCodes.Stloc, restLocal);
                        _il.Emit(OpCodes.Ldc_I4, i);
                        _il.Emit(OpCodes.Stloc, idxLocal);

                        _il.MarkLabel(loop);
                        _il.Emit(OpCodes.Ldloc, idxLocal);
                        _il.Emit(OpCodes.Ldloc, argsLocal);
                        _il.Emit(OpCodes.Ldlen);
                        _il.Emit(OpCodes.Conv_I4);
                        _il.Emit(OpCodes.Bge_S, done);

                        _il.Emit(OpCodes.Ldloc, restLocal);
                        _il.Emit(OpCodes.Ldloc, argsLocal);
                        _il.Emit(OpCodes.Ldloc, idxLocal);
                        _il.Emit(OpCodes.Ldelem_Ref);
                        _il.Emit(OpCodes.Callvirt, s_listAdd);

                        _il.Emit(OpCodes.Ldloc, idxLocal);
                        _il.Emit(OpCodes.Ldc_I4_1);
                        _il.Emit(OpCodes.Add);
                        _il.Emit(OpCodes.Stloc, idxLocal);
                        _il.Emit(OpCodes.Br_S, loop);

                        _il.MarkLabel(done);
                        _il.Emit(OpCodes.Ldloc, restLocal);
                        _il.Emit(OpCodes.Stloc, local);
                    }
                    else
                    {
                        var hasArg = _il.DefineLabel();
                        var loaded = _il.DefineLabel();

                        _il.Emit(OpCodes.Ldloc, argsLocal);
                        _il.Emit(OpCodes.Ldlen);
                        _il.Emit(OpCodes.Conv_I4);
                        _il.Emit(OpCodes.Ldc_I4, i);
                        _il.Emit(OpCodes.Bgt_S, hasArg);

                        _il.Emit(OpCodes.Ldsfld, s_compiledLambdaMissingArgument);
                        _il.Emit(OpCodes.Stloc, local);
                        _il.Emit(OpCodes.Br_S, loaded);

                        _il.MarkLabel(hasArg);
                        _il.Emit(OpCodes.Ldloc, argsLocal);
                        _il.Emit(OpCodes.Ldc_I4, i);
                        _il.Emit(OpCodes.Ldelem_Ref);
                        _il.Emit(OpCodes.Stloc, local);

                        _il.MarkLabel(loaded);
                    }

                    if (!parameter.IsRest && (parameter.IsOptional || parameter.Default is not null))
                    {
                        var hasValue = _il.DefineLabel();
                        _il.Emit(OpCodes.Ldloc, local);
                        _il.Emit(OpCodes.Ldsfld, s_compiledLambdaMissingArgument);
                        _il.Emit(OpCodes.Bne_Un, hasValue);

                        if (parameter.Default is not null)
                        {
                            var defaultType = EmitPipelineAsValue(parameter.Default);
                            if (defaultType is null)
                            {
                                _il.Emit(OpCodes.Ldnull);
                            }
                            else
                            {
                                BoxIfValueType(defaultType);
                            }
                        }
                        else
                        {
                            _il.Emit(OpCodes.Ldnull);
                        }

                        _il.Emit(OpCodes.Stloc, local);
                        _il.MarkLabel(hasValue);
                    }

                    _typedParamLocals[parameter.Symbol] = local;
                }
            }
            else for (var i = 0; i < func.Parameters.Count; i++)
            {
                if (entry.IsTyped)
                {
                    // Box typed value-type params (and pass through
                    // ref-type params) into an object-typed local so
                    // the rest of the body emitter — which assumes
                    // parameter loads return `object` — keeps
                    // working without per-shape coercion.
                    var local = _il.DeclareLocal(typeof(object));
                    _il.Emit(OpCodes.Ldarg, i);
                    var clr = entry.ParamClrTypes[i];
                    if (clr.IsValueType) _il.Emit(OpCodes.Box, clr);
                    // Refinement-check enforcement on parameter
                    // entry: if the declared parameter type is a
                    // refinement, route the boxed value through
                    // ToshHost.CheckType. Throws a runtime
                    // diagnostic when the annotation is violated.
                    if (func.Parameters[i].Symbol.DeclaredType is RefinementType refParam)
                    {
                        _il.Emit(OpCodes.Ldstr, refParam.Name);
                        _il.Emit(OpCodes.Ldc_I4, func.Parameters[i].Span.Start);
                        _il.Emit(OpCodes.Ldc_I4, func.Parameters[i].Span.Length);
                        _il.Emit(OpCodes.Ldstr, $"parameter '{func.Parameters[i].Name}'");
                        _il.Emit(OpCodes.Call, s_hostCheckType);
                    }
                    _il.Emit(OpCodes.Stloc, local);
                    _typedParamLocals[func.Parameters[i].Symbol] = local;
                }
                else
                {
                    _paramSlots[func.Parameters[i].Symbol] = i;
                }
            }
            // A trailing bare expression is the function's result — that is what
            // `func f(a: int) -> int => $a + 1` means, and what the interpreter does. The
            // rule now lives in `CollapseTrailingExpressionIntoReturn` because a class
            // method needs exactly the same one (`TOAST-0043`).
            var body = CollapseTrailingExpressionIntoReturn(func);

            EmitBlock(body);

            // Fall-through return: typed funcs must produce a default
            // value of the declared return type; untyped funcs keep
            // the legacy `Ldnull/Ret` semantics.
            if (entry.IsTyped)
            {
                EmitDefaultValueForType(entry.ReturnClrType);
            }
            else
            {
                _il.Emit(OpCodes.Ldnull);
            }
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
            _currentFunctionReturnTypeName = savedReturnTypeName;
            _currentFunctionReturnRefinement = savedReturnRefinement;
            _returnEmissionFrame = savedReturnEmissionFrame;
            _deferredCleanupFrames = savedDeferredCleanupFrames;
        }
    }

    /// <summary>
    /// Pushes a default-of-T value matching <paramref name="t"/>:
    /// numeric zero, false, default char, or null for ref types.
    /// Used for typed-function fall-through returns.
    /// </summary>
    private void EmitDefaultValueForType(Type t)
    {
        if (!t.IsValueType)
        {
            _il.Emit(OpCodes.Ldnull);
            return;
        }
        if (t == typeof(bool) || t == typeof(int) || t == typeof(short) ||
            t == typeof(byte) || t == typeof(sbyte) || t == typeof(uint) ||
            t == typeof(ushort) || t == typeof(char))
        {
            _il.Emit(OpCodes.Ldc_I4_0);
            return;
        }
        if (t == typeof(long) || t == typeof(ulong))
        {
            _il.Emit(OpCodes.Ldc_I4_0);
            _il.Emit(OpCodes.Conv_I8);
            return;
        }
        if (t == typeof(float))
        {
            _il.Emit(OpCodes.Ldc_R4, 0f);
            return;
        }
        if (t == typeof(double))
        {
            _il.Emit(OpCodes.Ldc_R8, 0d);
            return;
        }
        // Generic value-type fallback: `default(T)` via initobj.
        var slot = _il.DeclareLocal(t);
        _il.Emit(OpCodes.Ldloca, slot);
        _il.Emit(OpCodes.Initobj, t);
        _il.Emit(OpCodes.Ldloc, slot);
    }

}
