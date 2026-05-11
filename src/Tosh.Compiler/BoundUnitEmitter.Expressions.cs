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
    private Type? EmitExpression(BoundExpression expression)
    {
        switch (expression)
        {
            case BoundLiteral literal:
                return EmitLiteral(literal);

            case BoundVariableReference varRef:
                return EmitVariableReference(varRef);

            case BoundBinaryOperator binOp:
                return EmitBinaryOperator(binOp);

            case BoundUnaryOperator unOp:
                return EmitUnaryOperator(unOp);

            case BoundSubexpression sub:
                // (expr) — unwrap to the inner pipeline.
                return EmitPipelineAsValue(sub.Pipeline);

            case BoundInterpolatedString interp:
                return EmitInterpolatedString(interp);

            case BoundMemberAccess member:
                return EmitMemberAccess(member);

            case BoundStaticMemberAccess staticMember:
                return EmitStaticMemberAccess(staticMember);

            case BoundStaticMethodCall staticCall:
                return EmitStaticMethodCall(staticCall);

            case BoundIndexAccess index:
                return EmitIndexAccess(index);

            case BoundArrayLiteral arr:
                return EmitArrayLiteral(arr);

            case BoundRecordLiteral rec:
                return EmitRecordLiteral(rec);

            case BoundDictLiteral dict:
                return EmitDictLiteral(dict);

            case BoundSetLiteral set:
                return EmitSetLiteral(set);

            case BoundTupleLiteral tuple:
                return EmitTupleLiteral(tuple);

            case BoundCommandSubstitution cmdSub:
                return EmitPipelineAsValue(cmdSub.Pipeline);

            case BoundInputProcessSubstitution inSub:
                return EmitPipelineAsValue(inSub.Pipeline);

            case BoundOutputProcessSubstitution outSub:
                return EmitPipelineAsValue(outSub.Pipeline);

            case BoundMatchExpression match:
                return EmitMatchExpression(match);

            case BoundNewObject newObj:
                return EmitNewObject(newObj);

            case BoundMethodCall methodCall:
                return EmitMethodCall(methodCall);

            case BoundCallableInvocation callableInv:
                return EmitCallableInvocation(callableInv);

            case BoundLambda lambda:
                return EmitLambdaExpression(lambda);

            case BoundBlockExpression blockExpr:
                EmitMakeBlock(blockExpr);
                return typeof(object);

            case BoundRange range:
                return EmitRange(range);

            case BoundConditional cond:
                return EmitConditional(cond);

            case BoundIfExpression ifExpr:
                return EmitIfExpression(ifExpr);

            case BoundThrowExpression throwExpr:
                return EmitThrowExpression(throwExpr);

            case BoundNameOfExpression nameOf:
                return EmitNameOfExpression(nameOf);

            case BoundFunctionReference funcRef:
                return EmitFunctionReference(funcRef);

            case BoundMemberProjection proj:
                return EmitMemberProjection(proj);

            case BoundDynamicExpression dyn:
                Diagnostics.Add(
                    "compiled tosh: dynamic argument expressions ("
                    + dyn.Original.GetType().Name
                    + ") are not yet emitted");
                return null;

            default:
                Diagnostics.Add($"unsupported expression: {expression.GetType().Name}");
                return null;
        }
    }

    /// <summary>
    /// Coerces the value on the IL stack into a <c>bool</c>. Bools
    /// pass through; other types are boxed and routed through
    /// <see cref="global::Tosh.Compiler.Runtime.ToshHost.IsTruthy"/>.
    /// </summary>
    private void EmitTruthTest(Type valueType)
    {
        if (valueType == typeof(bool)) return;
        BoxIfValueType(valueType);
        _il.Emit(OpCodes.Call, s_hostIsTruthy);
    }

    /// <summary>
    /// Emits IL for a ternary <c>cond ? a : b</c>. Both branches are
    /// boxed to <see cref="object"/> so the resulting expression has
    /// a uniform type — the binder reports the ternary as
    /// <c>BoundType.Dynamic</c>.
    /// </summary>
    private Type? EmitConditional(BoundConditional cond)
    {
        var condType = EmitExpression(cond.Condition);
        if (condType is null) return null;
        EmitTruthTest(condType);

        var elseLabel = _il.DefineLabel();
        var endLabel = _il.DefineLabel();
        _il.Emit(OpCodes.Brfalse, elseLabel);

        var thenType = EmitExpression(cond.WhenTrue);
        if (thenType is null) return null;
        BoxIfValueType(thenType);
        _il.Emit(OpCodes.Br, endLabel);

        _il.MarkLabel(elseLabel);
        var elseType = EmitExpression(cond.WhenFalse);
        if (elseType is null) return null;
        BoxIfValueType(elseType);

        _il.MarkLabel(endLabel);
        return typeof(object);
    }

    /// <summary>
    /// Emits IL for an <c>if cond { … } else { … }</c> expression.
    /// Both branches are required (the binder only produces a
    /// <see cref="BoundIfExpression"/> when both arms are present).
    /// The block bodies' last pipeline becomes the branch value.
    /// </summary>
    private Type? EmitIfExpression(BoundIfExpression ifExpr)
    {
        var condType = EmitExpression(ifExpr.Condition);
        if (condType is null) return null;
        EmitTruthTest(condType);

        var resultLocal = _il.DeclareLocal(typeof(object));
        var elseLabel = _il.DefineLabel();
        var endLabel = _il.DefineLabel();
        _il.Emit(OpCodes.Brfalse, elseLabel);

        if (!EmitBlockAsValue(ifExpr.ThenBlock, resultLocal)) return null;
        _il.Emit(OpCodes.Br, endLabel);

        _il.MarkLabel(elseLabel);
        if (!EmitBlockAsValue(ifExpr.ElseBlock, resultLocal)) return null;

        _il.MarkLabel(endLabel);
        _il.Emit(OpCodes.Ldloc, resultLocal);
        return typeof(object);
    }

    /// <summary>
    /// Emits a block in value context: every leading statement runs
    /// normally and the trailing pipeline (or the last statement, if
    /// it's a pipeline statement) supplies the block's value, boxed
    /// to <see cref="object"/> and stored in <paramref name="result"/>.
    /// </summary>
    private bool EmitBlockAsValue(BoundBlock block, LocalBuilder result)
    {
        if (block.Statements.Count == 0)
        {
            _il.Emit(OpCodes.Ldnull);
            _il.Emit(OpCodes.Stloc, result);
            return true;
        }

        for (var i = 0; i < block.Statements.Count - 1; i++)
        {
            EmitStatement(block.Statements[i]);
        }

        var last = block.Statements[^1];
        if (last is BoundPipelineStatement pipeStmt)
        {
            var t = EmitPipelineAsValue(pipeStmt.Pipeline);
            if (t is null)
            {
                Diagnostics.Add("if-expression: trailing pipeline failed to emit as value");
                return false;
            }
            BoxIfValueType(t);
            _il.Emit(OpCodes.Stloc, result);
            return true;
        }

        // If the last statement isn't a pipeline (e.g. a return), emit
        // it normally — value context falls back to null.
        EmitStatement(last);
        _il.Emit(OpCodes.Ldnull);
        _il.Emit(OpCodes.Stloc, result);
        return true;
    }

    /// <summary>
    /// Emits IL for a <c>throw</c> expression in value context. The
    /// expression unconditionally throws, but it's typed as
    /// <c>object</c> so the IL stack discipline is consistent — we
    /// still emit a synthetic <c>ldnull</c> for verifier flow even
    /// though it's unreachable.
    /// </summary>
    private Type? EmitThrowExpression(BoundThrowExpression throwExpr)
    {
        if (throwExpr.Value is null)
        {
            // Re-throw in expression position is meaningless; reject
            // honestly rather than silently emitting `rethrow`.
            Diagnostics.Add("compiled tosh: bare `throw` is not valid in expression position");
            return null;
        }
        var t = EmitExpression(throwExpr.Value);
        if (t is null) return null;
        BoxIfValueType(t);
        // Wrap object → ToshUserException via host helper. The helper
        // is declared to return `object` so the verifier sees a value
        // left on the stack even though the call never returns
        // normally — do NOT emit an extra `ldnull` here.
        _il.Emit(OpCodes.Call, s_hostThrowAsException);
        return typeof(object);
    }

    /// <summary>
    /// Emits IL for <c>nameof(symbol)</c>: a constant string literal
    /// folded at lowering time.
    /// </summary>
    private Type? EmitNameOfExpression(BoundNameOfExpression nameOf)
    {
        _il.Emit(OpCodes.Ldstr, nameOf.Identifier);
        return typeof(string);
    }

    /// <summary>
    /// Emits IL for <c>&amp;funcname</c>. When the target resolves
    /// to exactly one user function compiled in this assembly, we
    /// bind directly to its <see cref="MethodInfo"/> through
    /// <c>ToshHost.MakeFunctionReferenceFromMethod</c> — that path
    /// works inside compiled assemblies where user functions are
    /// static methods rather than runtime <c>IShellCommand</c>
    /// entries. Otherwise (overloaded user funcs or builtin
    /// commands) we fall back to the late-binding by-name wrapper.
    /// </summary>
    private Type? EmitFunctionReference(BoundFunctionReference funcRef)
    {
        if (_userFunctions.TryGetValue(funcRef.Name, out var overloads)
            && overloads.Count == 1)
        {
            // ldtoken method; call MethodBase.GetFromHandle; castclass MethodInfo;
            // ldstr name; call host.MakeFunctionReferenceFromMethod
            _il.Emit(OpCodes.Ldtoken, overloads[0].Method);
            _il.Emit(OpCodes.Call, s_methodBaseGetFromHandle);
            _il.Emit(OpCodes.Castclass, typeof(MethodInfo));
            _il.Emit(OpCodes.Ldstr, funcRef.Name);
            RequireTier(2, "function reference (compiled method binding)");
            _il.Emit(OpCodes.Call, s_hostMakeFunctionReferenceFromMethod);
            return typeof(object);
        }
        if (overloads is not null && overloads.Count >= 2)
        {
            // Build MethodInfo[] containing every compiled overload.
            // ldc_i4 N; newarr MethodInfo;
            // for each overload i: dup; ldc_i4 i; ldtoken meth;
            //   call MethodBase.GetFromHandle; castclass MethodInfo; stelem.ref
            // ldstr name; call host.MakeFunctionReferenceFromMethods
            _il.Emit(OpCodes.Ldc_I4, overloads.Count);
            _il.Emit(OpCodes.Newarr, typeof(MethodInfo));
            for (var i = 0; i < overloads.Count; i++)
            {
                _il.Emit(OpCodes.Dup);
                _il.Emit(OpCodes.Ldc_I4, i);
                _il.Emit(OpCodes.Ldtoken, overloads[i].Method);
                _il.Emit(OpCodes.Call, s_methodBaseGetFromHandle);
                _il.Emit(OpCodes.Castclass, typeof(MethodInfo));
                _il.Emit(OpCodes.Stelem_Ref);
            }
            _il.Emit(OpCodes.Ldstr, funcRef.Name);
            RequireTier(2, "function reference (compiled overload set)");
            _il.Emit(OpCodes.Call, s_hostMakeFunctionReferenceFromMethods);
            return typeof(object);
        }
        _il.Emit(OpCodes.Ldstr, funcRef.Name);
        RequireTier(2, "function reference (late-bound name lookup)");
        _il.Emit(OpCodes.Call, s_hostMakeFunctionReference);
        return typeof(object);
    }

    /// <summary>
    /// Emits IL for <c>_.Path</c> projection. Produces a small
    /// callable wrapper via the host so it composes with pipeline
    /// stages (<c>each _.Path</c>) without source replay.
    /// </summary>
    private Type? EmitMemberProjection(BoundMemberProjection proj)
    {
        // path string[]: stack = string[]
        _il.Emit(OpCodes.Ldc_I4, proj.MemberPaths.Count);
        _il.Emit(OpCodes.Newarr, typeof(string));
        for (var i = 0; i < proj.MemberPaths.Count; i++)
        {
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldc_I4, i);
            _il.Emit(OpCodes.Ldstr, proj.MemberPaths[i]);
            _il.Emit(OpCodes.Stelem_Ref);
        }
        _il.Emit(OpCodes.Call, s_hostMakeMemberProjection);
        return typeof(object);
    }

    private Type? EmitLiteral(BoundLiteral literal)
    {
        switch (literal.Value)
        {
            case null:
                _il.Emit(OpCodes.Ldnull);
                return typeof(object);

            case string s:
                _il.Emit(OpCodes.Ldstr, s);
                return typeof(string);

            case bool b:
                _il.Emit(b ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                return typeof(bool);

            case int i:
                _il.Emit(OpCodes.Ldc_I4, i);
                return typeof(int);

            case long l:
                _il.Emit(OpCodes.Ldc_I8, l);
                return typeof(long);

            case double d:
                _il.Emit(OpCodes.Ldc_R8, d);
                return typeof(double);

            default:
                Diagnostics.Add($"unsupported literal type: {literal.Value.GetType().Name}");
                return null;
        }
    }

    private Type? EmitVariableReference(BoundVariableReference varRef)
    {
        if (varRef.Symbol is not null && _typedParamLocals.TryGetValue(varRef.Symbol, out var typedParamLocal))
        {
            // Typed user-function param materialized into an
            // object-typed local at method entry. Loads as object so
            // the rest of the body emitter — which assumes parameter
            // refs produce object — keeps working unchanged.
            _il.Emit(OpCodes.Ldloc, typedParamLocal);
            return typeof(object);
        }
        // Block-capture: the symbol was snapshotted into the captureValues
        // array (arg 1) at block-construction time. Load by index.
        if (varRef.Symbol is not null && _blockCaptureIndices.TryGetValue(varRef.Symbol, out var captureIdx))
        {
            _il.Emit(OpCodes.Ldarg_1);           // object[] _captureValues
            _il.Emit(OpCodes.Ldc_I4, captureIdx);
            _il.Emit(OpCodes.Ldelem_Ref);
            return typeof(object);
        }
        if (varRef.Symbol is not null && _paramSlots.TryGetValue(varRef.Symbol, out var paramIndex))
        {
            _il.Emit(OpCodes.Ldarg, paramIndex);
            return typeof(object);
        }
        if (varRef.Symbol is not null && _staticFields.TryGetValue(varRef.Symbol, out var captureField))
        {
            _il.Emit(OpCodes.Ldsfld, captureField);
            return typeof(object);
        }
        if (varRef.Symbol is null)
        {
            // Symbol-less references reach the emitter for special
            // names like the match-arm scrutinee placeholder `_`.
            if (string.Equals(varRef.Name, "_", StringComparison.Ordinal)
                && _underscoreStack.Count > 0)
            {
                _il.Emit(OpCodes.Ldloc, _underscoreStack.Peek());
                return typeof(object);
            }
            // Inside a compiled block-body method, `_` (no dollar) with no
            // outer-scope symbol is the pipeline item passed as arg 0.
            if (string.Equals(varRef.Name, "_", StringComparison.Ordinal)
                && _blockOutputLocal is not null)
            {
                _il.Emit(OpCodes.Ldarg_0);       // object? _item
                return typeof(object);
            }
            // `$this` inside a class-method body lowers to slot 0
            // typed as the shell. Member access / method calls then
            // pick up the shell's static type and lower to direct
            // ldfld / callvirt via the existing dispatch paths.
            if (string.Equals(varRef.Name, "this", StringComparison.Ordinal)
                && _currentThisType is not null)
            {
                _il.Emit(OpCodes.Ldarg_0);
                return _currentThisType;
            }
            Diagnostics.Add($"unresolved variable: {varRef.Name}");
            return null;
        }
        if (!_locals.TryGetValue(varRef.Symbol, out var slot))
        {
            Diagnostics.Add($"unresolved variable: {varRef.Name}");
            return null;
        }
        _il.Emit(OpCodes.Ldloc, slot.Local);
        return slot.Type;
    }

    /// <summary>
    /// Emits an interpolated string by building a <c>string[]</c> of
    /// each part's stringified value and calling
    /// <c>string.Concat(string[])</c>. Each part is either literal
    /// text or an expression hole whose value is converted to string
    /// via boxing + <c>object.ToString</c>.
    /// </summary>
    private Type? EmitInterpolatedString(BoundInterpolatedString interp)
    {
        var partCount = interp.Parts.Count;
        if (partCount == 0)
        {
            _il.Emit(OpCodes.Ldstr, string.Empty);
            return typeof(string);
        }

        if (partCount == 1 && interp.Parts[0] is BoundInterpolatedLiteral onlyLit)
        {
            _il.Emit(OpCodes.Ldstr, onlyLit.Text);
            return typeof(string);
        }

        _il.Emit(OpCodes.Ldc_I4, partCount);
        _il.Emit(OpCodes.Newarr, typeof(string));

        for (var i = 0; i < partCount; i++)
        {
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldc_I4, i);

            switch (interp.Parts[i])
            {
                case BoundInterpolatedLiteral lit:
                    _il.Emit(OpCodes.Ldstr, lit.Text);
                    break;

                case BoundInterpolatedExpression hole:
                    if (hole.Expression is null)
                    {
                        Diagnostics.Add($"interpolation hole has no bound expression: {hole.SourceText}");
                        _il.Emit(OpCodes.Ldstr, string.Empty);
                    }
                    else
                    {
                        var holeType = EmitExpression(hole.Expression);
                        if (holeType is null)
                        {
                            _il.Emit(OpCodes.Ldstr, string.Empty);
                        }
                        else
                        {
                            ConvertToString(holeType);
                        }
                    }
                    break;

                default:
                    Diagnostics.Add($"unsupported interpolated part: {interp.Parts[i].GetType().Name}");
                    _il.Emit(OpCodes.Ldstr, string.Empty);
                    break;
            }

            _il.Emit(OpCodes.Stelem_Ref);
        }

        var concatArray = typeof(string).GetMethod(
            nameof(string.Concat),
            new[] { typeof(string[]) })!;
        _il.Emit(OpCodes.Call, concatArray);
        return typeof(string);
    }

    /// <summary>
    /// Emits IL for <c>$target.path</c> / <c>$target?.path</c>. The
    /// dotted path is preserved verbatim; the runtime accessor
    /// walks each segment dynamically (matching the interpreter's
    /// behaviour). Always produces an <see cref="object"/> on the
    /// stack — refinement via cast happens at the use site.
    /// </summary>
    /// <summary>
    /// Emits a <c>new TypeName(args…)</c> expression by delegating
    /// to <see cref="global::Tosh.Compiler.Runtime.ToshHost.NewObject"/>.
    /// Named arguments are not yet supported in this position.
    /// </summary>
    private Type? EmitNewObject(BoundNewObject newObj)
    {
        // Direct lowering when a CLR shell exists for this type and
        // the call site arity matches the shell's primary ctor —
        // emits `newobj <ctor>` instead of routing through
        // ToshHost.NewObject. Result lands on the stack as the
        // typed shell type so member access / method calls on the
        // resulting local can take the typed paths too.
        if (_clrTypeShells.TryGetValue(newObj.TypeName, out var shell)
            && shell.SupportsDirectNewObj
            && newObj.Arguments.All(a => a.Name is null && !a.IsSplat))
        {
            if (shell.CtorParamTypes.Length == newObj.Arguments.Count)
            {
                for (int i = 0; i < newObj.Arguments.Count; i++)
                {
                    var argType = EmitExpression(newObj.Arguments[i].Value);
                    if (argType is null) return null;
                    BoxIfValueType(argType);
                }
                _il.Emit(OpCodes.Newobj, shell.Ctor);
                return shell.Type;
            }

            if (TryEmitRecordNewObjectWithDefaults(newObj, shell, out var recordType))
                return recordType;
        }

        _il.Emit(OpCodes.Ldstr, newObj.TypeName);
        // Build object?[] of arg values via the shared splat/named-aware emitter
        // so that `new TypeName(arg, name: value, ...rest)` flows through to
        // `ToshHost.NewObject`, which delegates to the engine's CreateInstance
        // path. The engine already understands `NamedArgument` wrappers.
        var hasTypeArgs = newObj.TypeArguments is { Count: > 0 };
        if (hasTypeArgs)
        {
            // Emit bare type name and the string[] of type arguments before
            // the args array so we can call the generic-aware overload.
            _il.Emit(OpCodes.Ldstr, newObj.BareTypeName ?? newObj.TypeName);
            var typeArgs = newObj.TypeArguments!;
            _il.Emit(OpCodes.Ldc_I4, typeArgs.Count);
            _il.Emit(OpCodes.Newarr, typeof(string));
            for (var i = 0; i < typeArgs.Count; i++)
            {
                _il.Emit(OpCodes.Dup);
                _il.Emit(OpCodes.Ldc_I4, i);
                _il.Emit(OpCodes.Ldstr, typeArgs[i]);
                _il.Emit(OpCodes.Stelem_Ref);
            }
        }
        if (!EmitArgsArrayCore($"new {newObj.TypeName}", newObj.Arguments)) return null;
        RequireTier(2, "new object construction via host dispatch");
        _il.Emit(OpCodes.Call, hasTypeArgs ? s_hostNewObjectGeneric : s_hostNewObject);
        return typeof(object);
    }

    private bool TryEmitRecordNewObjectWithDefaults(BoundNewObject newObj, ClrTypeShell shell, out Type? resultType)
    {
        resultType = null;

        if (!_clrRecordDefinitions.TryGetValue(newObj.TypeName, out var rec))
            return false;

        var providedCount = newObj.Arguments.Count;
        if (providedCount > rec.Fields.Count)
            return false;

        for (var i = providedCount; i < rec.Fields.Count; i++)
        {
            var missingField = rec.Fields[i];
            if (!missingField.IsOptional && missingField.DefaultValue is null)
                return false;
        }

        for (var i = 0; i < providedCount; i++)
        {
            var argType = EmitExpression(newObj.Arguments[i].Value);
            if (argType is null) return false;
            BoxIfValueType(argType);
        }

        for (var i = providedCount; i < rec.Fields.Count; i++)
        {
            var missingField = rec.Fields[i];
            if (missingField.DefaultValue is not null)
            {
                var defaultType = EmitPipeline(missingField.DefaultValue, asStatement: false);
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
        }

        _il.Emit(OpCodes.Newobj, shell.Ctor);
        resultType = shell.Type;
        return true;
    }

    private Type? EmitStaticMemberAccess(BoundStaticMemberAccess staticMember)
    {
        var path = staticMember.Path;
        var lastDot = path.LastIndexOf('.');
        if (lastDot > 0 && lastDot < path.Length - 1)
        {
            var unionName = path[..lastDot];
            var variantName = path[(lastDot + 1)..];
            if (_clrUnionShells.TryGetValue(unionName, out var shell)
                && shell.Variants.TryGetValue(variantName, out var variant)
                && variant.UnitSingletonField is not null)
            {
                _il.Emit(OpCodes.Ldsfld, variant.UnitSingletonField);
                return variant.Type;
            }

            // Direct-load path for non-integral / dynamic-value enum static
            // shells: `Color.Red` lowers to `ldsfld` against the emitted
            // public static readonly object field, with no engine call.
            if (_clrEnumStaticShells.TryGetValue(unionName, out var enumShell)
                && enumShell.Fields.TryGetValue(variantName, out var enumField))
            {
                _il.Emit(OpCodes.Ldsfld, enumField);
                return typeof(object);
            }
        }

        _il.Emit(OpCodes.Ldstr, staticMember.Path);
        RequireTier(2, "qualified-name resolution (Foo.bar)");
        _il.Emit(OpCodes.Call, s_hostResolveQualifiedAccess);
        return typeof(object);
    }

    /// <summary>
    /// Emits an instance method call <c>$target.Method(args)</c>.
    /// Routes through <see cref="global::Tosh.Compiler.Runtime.ToshHost.InvokeMember"/>
    /// so tosh-defined types and CLR types use the same dispatch
    /// surface. Named arguments aren't supported in this position.
    /// </summary>
    /// <summary>
    /// Emits IL for a dotted static method call like
    /// <c>Lib.greet()</c>. The host bridge resolves the path
    /// against modules, classes, and CLR types and dispatches the
    /// invocation.
    /// </summary>
    private Type? EmitStaticMethodCall(BoundStaticMethodCall call)
    {
        var lastDot = call.Path.LastIndexOf('.');
        if (lastDot > 0 && lastDot < call.Path.Length - 1)
        {
            var unionName = call.Path[..lastDot];
            var variantName = call.Path[(lastDot + 1)..];
            if (_clrUnionShells.TryGetValue(unionName, out var shell)
                && shell.Variants.TryGetValue(variantName, out var variant))
            {
                if (variant.UnitSingletonField is not null && call.Arguments.Count == 0)
                {
                    _il.Emit(OpCodes.Ldsfld, variant.UnitSingletonField);
                    return variant.Type;
                }

                if (variant.Fields.Count == call.Arguments.Count)
                {
                    var allPositional = true;
                    foreach (var arg in call.Arguments)
                    {
                        if (arg.Name is not null || arg.IsSplat) { allPositional = false; break; }
                    }
                    if (allPositional)
                    {
                        for (var i = 0; i < call.Arguments.Count; i++)
                        {
                            var arg = call.Arguments[i];
                            var at = EmitExpression(arg.Value);
                            if (at is null) return null;
                            BoxIfValueType(at);
                        }

                        _il.Emit(OpCodes.Newobj, variant.Ctor);
                        return variant.Type;
                    }
                    // Named/splat union construction: fall through to the host
                    // path (`s_hostInvokeQualifiedMethod`) below, which the
                    // engine resolves against `ToshUnionDefinition.CreateInstance`.
                }
            }
        }

        _il.Emit(OpCodes.Ldstr, call.Path);
        if (!EmitArgsArrayCore($"static method '{call.Path}'", call.Arguments)) return null;
        RequireTier(2, "qualified-method invocation (Foo.bar(...))");
        _il.Emit(OpCodes.Call, s_hostInvokeQualifiedMethod);
        return typeof(object);
    }

    private Type? EmitMethodCall(BoundMethodCall call)
    {
        var t = EmitExpression(call.Target);
        if (t is null) return null;
        BoxIfValueType(t);

        // Fast path: target's static type is a CLR shell with the
        // method declared on it. We bypass ToshHost.InvokeMember and
        // emit a direct callvirt against the shell's MethodBuilder.
        // Rejected when:
        //   - any argument is named (the trampoline doesn't model
        //     keyword args; let the host bridge do the runtime
        //     fallback);
        //   - the call site uses null-safe access (preserve the
        //     host's null check);
        //   - argument count doesn't match the trampoline arity.
        if (!call.NullSafe
            && _clrShellsByType.TryGetValue(t, out var shell)
            && shell.Methods.TryGetValue(call.MethodName, out var mb)
            && mb.GetParameters().Length == call.Arguments.Count)
        {
            var allPositional = true;
            foreach (var arg in call.Arguments)
            {
                if (arg.Name is not null) { allPositional = false; break; }
            }
            if (allPositional)
            {
                // Push args; box value types, leave reference types
                // as-is. Method is `object`-typed so no coercion.
                foreach (var arg in call.Arguments)
                {
                    var at = EmitExpression(arg.Value);
                    if (at is null) return null;
                    BoxIfValueType(at);
                }
                _il.Emit(OpCodes.Callvirt, mb);
                return typeof(object);
            }
        }

        _il.Emit(OpCodes.Ldstr, call.MethodName);
        if (!EmitArgsArrayCore($"method '{call.MethodName}'", call.Arguments)) return null;
        _il.Emit(call.NullSafe ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
        RequireTier(2, "dynamic member access");
        _il.Emit(OpCodes.Call, s_hostInvokeMember);
        return typeof(object);
    }

    private Type? EmitCallableInvocation(BoundCallableInvocation inv)
    {
        var targetType = EmitExpression(inv.Target);
        if (targetType is null) return null;
        BoxIfValueType(targetType);

        // Reuse the same argument materialization path as command calls
        // so named args / splats behave the same in compiled mode.
        var shimCall = new BoundCommandCall(
            "<callable>",
            inv.Span,
            null,
            inv.Arguments,
            inv.Span);
        if (!EmitArgsArray(shimCall)) return null;

        _il.Emit(OpCodes.Call, s_hostInvokeCallable);
        return typeof(object);
    }

    private Type? EmitLambdaExpression(BoundLambda lambda)
    {
        var runtimeCaptures = new List<BoundSymbol>(lambda.Captures.Count);
        foreach (var c in lambda.Captures)
        {
            if (!_staticFields.ContainsKey(c)) runtimeCaptures.Add(c);
        }

        var captureIndices = new Dictionary<BoundSymbol, int>(runtimeCaptures.Count);
        for (var i = 0; i < runtimeCaptures.Count; i++)
        {
            captureIndices[runtimeCaptures[i]] = i;
        }

        var lambdaMethod = EmitLambdaBodyMethod(lambda, captureIndices);
        if (lambdaMethod is null) return null;

        _il.Emit(OpCodes.Ldnull);
        _il.Emit(OpCodes.Ldftn, lambdaMethod);
        _il.Emit(OpCodes.Newobj, s_funcLambdaBodyCtor);

        _il.Emit(OpCodes.Ldc_I4, runtimeCaptures.Count);
        _il.Emit(OpCodes.Newarr, typeof(object));
        for (var i = 0; i < runtimeCaptures.Count; i++)
        {
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldc_I4, i);
            var cap = runtimeCaptures[i];
            if (_typedParamLocals.TryGetValue(cap, out var typedLocal))
            {
                _il.Emit(OpCodes.Ldloc, typedLocal);
            }
            else if (_paramSlots.TryGetValue(cap, out var pIdx))
            {
                _il.Emit(OpCodes.Ldarg, pIdx);
            }
            else if (_staticFields.TryGetValue(cap, out var sf))
            {
                _il.Emit(OpCodes.Ldsfld, sf);
            }
            else if (_locals.TryGetValue(cap, out var s))
            {
                _il.Emit(OpCodes.Ldloc, s.Local);
                BoxIfValueType(s.Type);
            }
            else
            {
                Diagnostics.Add($"lambda capture '{cap.Name}' has no IL slot");
                _il.Emit(OpCodes.Ldnull);
            }
            _il.Emit(OpCodes.Stelem_Ref);
        }

        _il.Emit(OpCodes.Ldc_I4, lambda.Parameters.Count);
        _il.Emit(OpCodes.Newarr, typeof(string));
        for (var i = 0; i < lambda.Parameters.Count; i++)
        {
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldc_I4, i);
            _il.Emit(OpCodes.Ldstr, lambda.Parameters[i].Name);
            _il.Emit(OpCodes.Stelem_Ref);
        }

        _il.Emit(OpCodes.Ldc_I4, lambda.Parameters.Count);
        _il.Emit(OpCodes.Newarr, typeof(bool));
        for (var i = 0; i < lambda.Parameters.Count; i++)
        {
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldc_I4, i);
            _il.Emit(lambda.Parameters[i].IsOptional ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
            _il.Emit(OpCodes.Stelem_I1);
        }

        var requiredCount = 0;
        for (var i = 0; i < lambda.Parameters.Count; i++)
        {
            if (!lambda.Parameters[i].IsOptional && !lambda.Parameters[i].IsRest)
            {
                requiredCount++;
            }
        }

        _il.Emit(OpCodes.Ldc_I4, requiredCount);

        var hasRest = lambda.Parameters.Count > 0 && lambda.Parameters[^1].IsRest;
        _il.Emit(OpCodes.Ldc_I4, hasRest ? -1 : lambda.Parameters.Count);

        var restIndex = -1;
        for (var i = 0; i < lambda.Parameters.Count; i++)
        {
            if (lambda.Parameters[i].IsRest)
            {
                restIndex = i;
                break;
            }
        }

        _il.Emit(OpCodes.Ldc_I4, restIndex);
        _il.Emit(OpCodes.Call, s_hostMakeCompiledLambda);
        return typeof(object);
    }

    private MethodBuilder? EmitLambdaBodyMethod(
        BoundLambda lambda,
        Dictionary<BoundSymbol, int> captureIndices)
    {
        var methodName = $"__lambda_{lambda.Span.Start}";
        var lambdaMethod = _program.DefineMethod(
            methodName,
            MethodAttributes.Private | MethodAttributes.Static,
            typeof(List<object>),
            new[] { typeof(object[]), typeof(object[]) });

        var savedIl = _il;
        var savedLocals = _locals;
        var savedParams = _paramSlots;
        var savedTypedParams = _typedParamLocals;
        var savedReturnType = _currentFunctionReturnType;
        var savedReturnRefinement = _currentFunctionReturnRefinement;
        var savedThisType = _currentThisType;
        var savedUnderscoreStack = _underscoreStack;
        var savedLoopStack = _loopStack;
        var savedBlockOutput = _blockOutputLocal;
        var savedBlockCaptures = _blockCaptureIndices;
        try
        {
            _il = lambdaMethod.GetILGenerator();
            _locals = new();
            _paramSlots = new();
            _typedParamLocals = new();
            _currentFunctionReturnType = null;
            _currentFunctionReturnRefinement = null;
            _currentThisType = null;
            _underscoreStack = new();
            _loopStack = new();
            _blockCaptureIndices = captureIndices;

            var resultsLocal = _il.DeclareLocal(typeof(List<object>));
            _blockOutputLocal = resultsLocal;
            _il.Emit(OpCodes.Newobj, s_listCtor);
            _il.Emit(OpCodes.Stloc, resultsLocal);

            for (var i = 0; i < lambda.Parameters.Count; i++)
            {
                var parameter = lambda.Parameters[i];
                var local = _il.DeclareLocal(typeof(object));
                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Ldc_I4, i);
                _il.Emit(OpCodes.Ldelem_Ref);
                _il.Emit(OpCodes.Stloc, local);

                if (parameter.IsOptional && parameter.Default is not null)
                {
                    var hasValue = _il.DefineLabel();
                    _il.Emit(OpCodes.Ldloc, local);
                    _il.Emit(OpCodes.Ldsfld, s_compiledLambdaMissingArgument);
                    _il.Emit(OpCodes.Bne_Un_S, hasValue);

                    var defType = EmitPipelineAsValue(parameter.Default);
                    if (defType is null) return null;
                    BoxIfValueType(defType);
                    _il.Emit(OpCodes.Stloc, local);

                    _il.MarkLabel(hasValue);
                }

                _typedParamLocals[parameter.Symbol] = local;
            }

            foreach (var stmt in lambda.Body.Statements)
            {
                EmitStatement(stmt);
            }

            _il.Emit(OpCodes.Ldloc, resultsLocal);
            _il.Emit(OpCodes.Ret);
            return lambdaMethod;
        }
        catch
        {
            return null;
        }
        finally
        {
            _il = savedIl;
            _locals = savedLocals;
            _paramSlots = savedParams;
            _typedParamLocals = savedTypedParams;
            _currentFunctionReturnType = savedReturnType;
            _currentFunctionReturnRefinement = savedReturnRefinement;
            _currentThisType = savedThisType;
            _underscoreStack = savedUnderscoreStack;
            _loopStack = savedLoopStack;
            _blockOutputLocal = savedBlockOutput;
            _blockCaptureIndices = savedBlockCaptures;
        }
    }

    private Type? EmitMemberAccess(BoundMemberAccess member)
    {
        // Direct ldfld when target produces a known CLR shell type
        // and the member path is a single segment naming a public
        // field on that shell. Multi-segment paths (e.g. "a.b.c")
        // and missing fields fall back to the dynamic
        // ToshHost.GetMember path. Null-safe access also stays on
        // the dynamic path so the host's null check is preserved.
        if (!member.NullSafe && !member.MemberPath.Contains('.'))
        {
            // Peek at target's static type via a no-side-effect
            // emission of the target expression. We need the type
            // BEFORE emitting, so we emit, check, and either commit
            // (ldfld) or wrap into the host call. The target was
            // already pushed onto the stack here.
            var t = EmitExpression(member.Target);
            if (t is null) return null;
            if (_clrShellsByType.TryGetValue(t, out var shell)
                && shell.Fields.TryGetValue(member.MemberPath, out var field))
            {
                _il.Emit(OpCodes.Ldfld, field);
                return typeof(object);
            }
            // Fall through to host dispatch with target still on stack.
            BoxIfValueType(t);
            _il.Emit(OpCodes.Ldstr, member.MemberPath);
            _il.Emit(OpCodes.Ldc_I4_0);
            _il.Emit(OpCodes.Call, s_hostGetMember);
            return typeof(object);
        }

        var t2 = EmitExpression(member.Target);
        if (t2 is null) return null;
        BoxIfValueType(t2);
        _il.Emit(OpCodes.Ldstr, member.MemberPath);
        _il.Emit(member.NullSafe ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
        _il.Emit(OpCodes.Call, s_hostGetMember);
        return typeof(object);
    }

    /// <summary>
    /// Emits IL for <c>$target[index]</c>. The host shim uses the
    /// runtime's <c>ShellIndexingUtilities.GetIndexedValue</c> so
    /// behaviour matches the interpreter for lists, dicts, strings,
    /// and CLR indexers. <see cref="IndexLookupKind"/> beyond the
    /// default isn't yet plumbed through.
    /// </summary>
    private Type? EmitIndexAccess(BoundIndexAccess index)
    {
        if (index.LookupKind != global::Tosh.Runtime.IndexLookupKind.Default)
        {
            Diagnostics.Add(
                $"index lookup kind '{index.LookupKind}' not yet supported");
            return null;
        }
        var tt = EmitExpression(index.Target);
        if (tt is null) return null;
        BoxIfValueType(tt);
        var ti = EmitExpression(index.Index);
        if (ti is null) return null;
        BoxIfValueType(ti);
        _il.Emit(OpCodes.Call, s_hostGetIndex);
        return typeof(object);
    }

    /// <summary>
    /// Emits a range literal as a <see cref="global::Tosh.Runtime.ToshRange"/>
    /// instance. Each bound is converted to <c>int</c> via
    /// <see cref="Convert.ToInt32(object)"/> so non-int sources
    /// (e.g. doubles or strings) follow the same coercion path the
    /// interpreter uses. Missing <c>Step</c> / <c>End</c> push
    /// default(<c>int?</c>).
    /// </summary>
    private Type? EmitRange(BoundRange range)
    {
        var startT = EmitExpression(range.Start);
        if (startT is null) return null;
        if (startT != typeof(int))
        {
            BoxIfValueType(startT);
            _il.Emit(OpCodes.Call, s_convertToInt32);
        }

        EmitNullableInt(range.Step);
        EmitNullableInt(range.End);

        _il.Emit(OpCodes.Newobj, s_toshRangeCtor);
        return typeof(global::Tosh.Runtime.ToshRange);
    }

    private void EmitNullableInt(BoundExpression? expr)
    {
        if (expr is null)
        {
            var loc = _il.DeclareLocal(typeof(int?));
            _il.Emit(OpCodes.Ldloca, loc);
            _il.Emit(OpCodes.Initobj, typeof(int?));
            _il.Emit(OpCodes.Ldloc, loc);
            return;
        }

        var t = EmitExpression(expr);
        if (t is null)
        {
            // Diagnostic already added; emit a default so IL stays balanced.
            var loc = _il.DeclareLocal(typeof(int?));
            _il.Emit(OpCodes.Ldloca, loc);
            _il.Emit(OpCodes.Initobj, typeof(int?));
            _il.Emit(OpCodes.Ldloc, loc);
            return;
        }
        if (t != typeof(int))
        {
            BoxIfValueType(t);
            _il.Emit(OpCodes.Call, s_convertToInt32);
        }
        _il.Emit(OpCodes.Newobj, s_nullableInt32Ctor);
    }

    /// <summary>
    /// Emits a list literal as <c>new List&lt;object?&gt;()</c>
    /// followed by <c>Add</c> calls for each item. Spread elements
    /// (<c>...$xs</c>) reuse <see cref="global::Tosh.Compiler.Runtime.ToshHost.SpreadArgs"/>:
    /// the source value is enumerated and each element pushed into
    /// the same backing list.
    /// </summary>
    private Type? EmitArrayLiteral(BoundArrayLiteral arr)
    {
        _il.Emit(OpCodes.Newobj, s_listCtor);
        foreach (var item in arr.Items)
        {
            _il.Emit(OpCodes.Dup);
            var t = EmitExpression(item.Value);
            if (t is null) return null;
            BoxIfValueType(t);
            if (item.IsSpread)
            {
                // Stack: list, list, value -> SpreadArgs(list, value)
                _il.Emit(OpCodes.Call, s_hostSpreadArgs);
            }
            else
            {
                _il.Emit(OpCodes.Callvirt, s_listAdd);
            }
        }
        return s_listOfObject;
    }

    /// <summary>
    /// Emits a record literal (<c>{ name: "x", age: 1, ...$rest, [computed]: v }</c>)
    /// as <c>new Dictionary&lt;string, object?&gt;()</c> with one
    /// indexer-set per field, host-routed merge per spread entry,
    /// and a stringified key for computed-name entries. Order is
    /// preserved so later entries overwrite earlier ones —
    /// matching the interpreter's left-to-right merge semantics.
    /// </summary>
    private Type? EmitRecordLiteral(BoundRecordLiteral rec)
    {
        _il.Emit(OpCodes.Newobj, s_dictCtor);
        foreach (var entry in rec.Fields)
        {
            switch (entry)
            {
                case BoundRecordField field:
                    _il.Emit(OpCodes.Dup);
                    _il.Emit(OpCodes.Ldstr, field.Name);
                    var vt = EmitExpression(field.Value);
                    if (vt is null) return null;
                    BoxIfValueType(vt);
                    _il.Emit(OpCodes.Callvirt, s_dictSetItem);
                    break;

                case BoundComputedRecordField computed:
                    _il.Emit(OpCodes.Dup);
                    var nt = EmitExpression(computed.NameExpression);
                    if (nt is null) return null;
                    BoxIfValueType(nt);
                    _il.Emit(OpCodes.Callvirt, s_objectToString);
                    var cvt = EmitExpression(computed.Value);
                    if (cvt is null) return null;
                    BoxIfValueType(cvt);
                    _il.Emit(OpCodes.Callvirt, s_dictSetItem);
                    break;

                case BoundRecordSpreadEntry spread:
                    // Stack: dict, dict, source -> SpreadRecord(dict, source)
                    _il.Emit(OpCodes.Dup);
                    var st = EmitExpression(spread.Value);
                    if (st is null) return null;
                    BoxIfValueType(st);
                    RequireTier(2, "record spread (...$record)");
                    _il.Emit(OpCodes.Call, s_hostSpreadRecord);
                    break;

                default:
                    Diagnostics.Add(
                        $"record literal: '{entry.GetType().Name}' entries not yet supported");
                    return null;
            }
        }
        return s_dictOfStringObject;
    }

    /// <summary>
    /// Emits a dict literal (<c>{ "k" =&gt; v, ... }</c>) as
    /// <c>new Dictionary&lt;object, object?&gt;()</c> populated via
    /// the indexer setter. Keys are evaluated as expressions and
    /// boxed.
    /// </summary>
    private Type? EmitDictLiteral(BoundDictLiteral dict)
    {
        _il.Emit(OpCodes.Newobj, s_dictObjCtor);
        foreach (var entry in dict.Entries)
        {
            _il.Emit(OpCodes.Dup);
            var kt = EmitExpression(entry.Key);
            if (kt is null) return null;
            BoxIfValueType(kt);
            var vt = EmitExpression(entry.Value);
            if (vt is null) return null;
            BoxIfValueType(vt);
            _il.Emit(OpCodes.Callvirt, s_dictObjSetItem);
        }
        return s_dictOfObjectObject;
    }

    private Type? EmitSetLiteral(BoundSetLiteral set)
    {
        _il.Emit(OpCodes.Newobj, s_hashSetCtor);
        foreach (var item in set.Items)
        {
            _il.Emit(OpCodes.Dup);
            var t = EmitExpression(item);
            if (t is null) return null;
            BoxIfValueType(t);
            _il.Emit(OpCodes.Callvirt, s_hashSetAdd);
            _il.Emit(OpCodes.Pop);
        }
        return s_hashSetOfObject;
    }

    private Type? EmitTupleLiteral(BoundTupleLiteral tuple)
    {
        _il.Emit(OpCodes.Ldc_I4, tuple.Items.Count);
        _il.Emit(OpCodes.Newarr, typeof(object));
        for (var i = 0; i < tuple.Items.Count; i++)
        {
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldc_I4, i);
            var t = EmitExpression(tuple.Items[i]);
            if (t is null) return null;
            BoxIfValueType(t);
            _il.Emit(OpCodes.Stelem_Ref);
        }
        _il.Emit(OpCodes.Newobj, s_toshTupleCtor);
        return s_toshTupleType;
    }

    private Type? EmitBinaryOperator(BoundBinaryOperator binOp)
    {
        // Short-circuit operators must guard the right-hand side, so
        // they cannot use the eager left/right emission path below.
        switch (binOp.Operator)
        {
            case "??": return EmitNullCoalesce(binOp);
            case "and": return EmitLogicalAnd(binOp);
            case "or": return EmitLogicalOr(binOp);
        }

        // String concat: "a" + b → string.Concat(a, b.ToString()).
        if (binOp.Operator == "+")
        {
            var leftType = EmitExpression(binOp.Left);
            if (leftType is null) return null;
            if (leftType == typeof(string))
            {
                var rightType = EmitExpression(binOp.Right);
                if (rightType is null) return null;
                ConvertToString(rightType);
                _il.Emit(OpCodes.Call, typeof(string).GetMethod(
                    nameof(string.Concat),
                    new[] { typeof(string), typeof(string) })!);
                return typeof(string);
            }

            // Numeric path.
            var rightTypeNum = EmitExpression(binOp.Right);
            if (rightTypeNum is null) return null;
            return EmitNumericArith("+", leftType, rightTypeNum);
        }

        var l = EmitExpression(binOp.Left);
        if (l is null) return null;
        var r = EmitExpression(binOp.Right);
        if (r is null) return null;

        switch (binOp.Operator)
        {
            case "-":
            case "*":
            case "/":
            case "%":
                return EmitNumericArith(binOp.Operator, l, r);

            case "==":
                EmitEquality(l, r, invert: false);
                return typeof(bool);
            case "!=":
                EmitEquality(l, r, invert: true);
                return typeof(bool);
            case "<":
                EmitComparison(l, r, OpCodes.Clt);
                return typeof(bool);
            case ">":
                EmitComparison(l, r, OpCodes.Cgt);
                return typeof(bool);
            case "<=":
                // !(l > r)
                EmitComparison(l, r, OpCodes.Cgt);
                _il.Emit(OpCodes.Ldc_I4_0);
                _il.Emit(OpCodes.Ceq);
                return typeof(bool);
            case ">=":
                // !(l < r)
                EmitComparison(l, r, OpCodes.Clt);
                _il.Emit(OpCodes.Ldc_I4_0);
                _il.Emit(OpCodes.Ceq);
                return typeof(bool);

            default:
                // Generic operators (`**`, `//`, `=~`, `!~`, `in`,
                // `contains`, `starts-with`, `ends-with`, `is`, `as`,
                // …) defer to OperatorEvaluator.EvaluateBinary at
                // runtime so the compiler stays semantics-aligned with
                // the engine.
                return EmitBinaryOperatorFallback(l, binOp.Operator, r);
        }
    }

    /// <summary>
    /// Emits a runtime call to <see
    /// cref="global::Tosh.Runtime.OperatorEvaluator.EvaluateBinary"/>
    /// using the values already on the IL stack (left below, right
    /// on top). Boxes value types as needed and returns
    /// <see cref="object"/>.
    /// </summary>
    private Type EmitBinaryOperatorFallback(Type l, string op, Type r)
    {
        // Stack: ..., left, right
        if (r.IsValueType) _il.Emit(OpCodes.Box, r);
        var rTemp = _il.DeclareLocal(typeof(object));
        _il.Emit(OpCodes.Stloc, rTemp);
        if (l.IsValueType) _il.Emit(OpCodes.Box, l);
        _il.Emit(OpCodes.Ldstr, op);
        _il.Emit(OpCodes.Ldloc, rTemp);
        _il.Emit(OpCodes.Call, s_opEvaluateBinary);
        return typeof(object);
    }

    /// <summary>
    /// Emits short-circuit <c>??</c>: evaluate left; if non-null, use
    /// it; otherwise evaluate right. Value-typed left collapses to
    /// the left value (never null).
    /// </summary>
    private Type? EmitNullCoalesce(BoundBinaryOperator binOp)
    {
        var leftType = EmitExpression(binOp.Left);
        if (leftType is null) return null;

        if (leftType.IsValueType)
        {
            // Value types are never null; left is the result and the
            // right operand is unreachable. Box for uniform `object`
            // result so consumers do not need to special-case.
            _il.Emit(OpCodes.Box, leftType);
            return typeof(object);
        }

        var done = _il.DefineLabel();
        _il.Emit(OpCodes.Dup);
        _il.Emit(OpCodes.Brtrue_S, done);
        _il.Emit(OpCodes.Pop);
        var rightType = EmitExpression(binOp.Right);
        if (rightType is null) return null;
        if (rightType.IsValueType) _il.Emit(OpCodes.Box, rightType);
        _il.MarkLabel(done);
        return typeof(object);
    }

    /// <summary>
    /// Emits short-circuit <c>and</c>: <c>ToBoolean(left) &amp;&amp;
    /// ToBoolean(right)</c>. Right operand is only evaluated when
    /// left is truthy.
    /// </summary>
    private Type? EmitLogicalAnd(BoundBinaryOperator binOp)
    {
        var leftType = EmitExpression(binOp.Left);
        if (leftType is null) return null;
        EmitConvertToBoolean(leftType);
        var falsey = _il.DefineLabel();
        var done = _il.DefineLabel();
        _il.Emit(OpCodes.Brfalse_S, falsey);
        var rightType = EmitExpression(binOp.Right);
        if (rightType is null) return null;
        EmitConvertToBoolean(rightType);
        _il.Emit(OpCodes.Br_S, done);
        _il.MarkLabel(falsey);
        _il.Emit(OpCodes.Ldc_I4_0);
        _il.MarkLabel(done);
        return typeof(bool);
    }

    /// <summary>
    /// Emits short-circuit <c>or</c>: <c>ToBoolean(left) ||
    /// ToBoolean(right)</c>. Right operand is only evaluated when
    /// left is falsey.
    /// </summary>
    private Type? EmitLogicalOr(BoundBinaryOperator binOp)
    {
        var leftType = EmitExpression(binOp.Left);
        if (leftType is null) return null;
        EmitConvertToBoolean(leftType);
        var truthy = _il.DefineLabel();
        var done = _il.DefineLabel();
        _il.Emit(OpCodes.Brtrue_S, truthy);
        var rightType = EmitExpression(binOp.Right);
        if (rightType is null) return null;
        EmitConvertToBoolean(rightType);
        _il.Emit(OpCodes.Br_S, done);
        _il.MarkLabel(truthy);
        _il.Emit(OpCodes.Ldc_I4_1);
        _il.MarkLabel(done);
        return typeof(bool);
    }

    /// <summary>
    /// Coerces the value on top of the IL stack to <see cref="bool"/>
    /// using <see
    /// cref="global::Tosh.Runtime.OperatorEvaluator.ToBoolean"/> for
    /// non-bool inputs. Box value types before the call.
    /// </summary>
    private void EmitConvertToBoolean(Type t)
    {
        if (t == typeof(bool)) return;
        if (t.IsValueType) _il.Emit(OpCodes.Box, t);
        _il.Emit(OpCodes.Call, s_opToBoolean);
    }

    /// <summary>
    /// Emits arithmetic between two numeric operands already on the
    /// stack (left below, right on top). Coerces both to the smallest
    /// common numeric type and emits the matching IL opcode.
    /// </summary>
    private Type? EmitNumericArith(string op, Type left, Type right)
    {
        var common = CommonNumericType(left, right);
        if (common is null)
        {
            Diagnostics.Add($"non-numeric operands to '{op}': {left.Name} and {right.Name}");
            return null;
        }

        // Right is on top; convert if needed.
        if (right != common) ConvertNumeric(right, common);

        // Left is below right; need to reorder to convert it.
        if (left != common)
        {
            var temp = _il.DeclareLocal(common);
            _il.Emit(OpCodes.Stloc, temp);
            ConvertNumeric(left, common);
            _il.Emit(OpCodes.Ldloc, temp);
        }

        switch (op)
        {
            case "+": _il.Emit(OpCodes.Add); break;
            case "-": _il.Emit(OpCodes.Sub); break;
            case "*": _il.Emit(OpCodes.Mul); break;
            case "/": _il.Emit(OpCodes.Div); break;
            case "%": _il.Emit(OpCodes.Rem); break;
            default:
                Diagnostics.Add($"unsupported numeric op: '{op}'");
                return null;
        }
        return common;
    }

    private void EmitEquality(Type left, Type right, bool invert)
    {
        if (IsNumericType(left) && IsNumericType(right))
        {
            var common = CommonNumericType(left, right)!;
            if (right != common) ConvertNumeric(right, common);
            if (left != common)
            {
                var temp = _il.DeclareLocal(common);
                _il.Emit(OpCodes.Stloc, temp);
                ConvertNumeric(left, common);
                _il.Emit(OpCodes.Ldloc, temp);
            }
            _il.Emit(OpCodes.Ceq);
        }
        else
        {
            // Box value types and call object.Equals(a, b).
            if (right.IsValueType) _il.Emit(OpCodes.Box, right);
            var rTemp = _il.DeclareLocal(typeof(object));
            _il.Emit(OpCodes.Stloc, rTemp);
            if (left.IsValueType) _il.Emit(OpCodes.Box, left);
            _il.Emit(OpCodes.Ldloc, rTemp);
            _il.Emit(OpCodes.Call, s_objectEquals);
        }
        if (invert)
        {
            _il.Emit(OpCodes.Ldc_I4_0);
            _il.Emit(OpCodes.Ceq);
        }
    }

    private void EmitComparison(Type left, Type right, OpCode op)
    {
        var common = CommonNumericType(left, right);
        if (common is null)
        {
            Diagnostics.Add($"non-numeric operands to comparison: {left.Name} and {right.Name}");
            return;
        }
        if (right != common) ConvertNumeric(right, common);
        if (left != common)
        {
            var temp = _il.DeclareLocal(common);
            _il.Emit(OpCodes.Stloc, temp);
            ConvertNumeric(left, common);
            _il.Emit(OpCodes.Ldloc, temp);
        }
        _il.Emit(op);
    }

    private Type? EmitUnaryOperator(BoundUnaryOperator unOp)
    {
        var operandType = EmitExpression(unOp.Operand);
        if (operandType is null) return null;

        switch (unOp.Operator)
        {
            case "-":
                if (operandType == typeof(object))
                {
                    // Coerce to long; users wanting double semantics can
                    // multiply by a double literal first.
                    _il.Emit(OpCodes.Call, s_convertToInt64);
                    operandType = typeof(long);
                }
                if (!IsNumericType(operandType))
                {
                    Diagnostics.Add($"unary '-' on non-numeric: {operandType.Name}");
                    return null;
                }
                _il.Emit(OpCodes.Neg);
                return operandType;

            case "!":
                if (operandType != typeof(bool))
                {
                    Diagnostics.Add($"unary '!' on non-bool: {operandType.Name}");
                    return null;
                }
                _il.Emit(OpCodes.Ldc_I4_0);
                _il.Emit(OpCodes.Ceq);
                return typeof(bool);

            default:
                // Unknown unary (e.g., `not`) defers to
                // OperatorEvaluator.EvaluateUnary at runtime so the
                // compiler stays semantics-aligned with the engine.
                if (operandType.IsValueType) _il.Emit(OpCodes.Box, operandType);
                var operandLocal = _il.DeclareLocal(typeof(object));
                _il.Emit(OpCodes.Stloc, operandLocal);
                _il.Emit(OpCodes.Ldstr, unOp.Operator);
                _il.Emit(OpCodes.Ldloc, operandLocal);
                _il.Emit(OpCodes.Call, s_opEvaluateUnary);
                return typeof(object);
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────

    private static bool IsNumericType(Type t) =>
        t == typeof(int) || t == typeof(long) || t == typeof(double);

    /// <summary>
    /// Like <see cref="IsNumericType"/> but also accepts
    /// <see cref="object"/>. Object-typed slots show up whenever a
    /// value comes from a function parameter or a function-call
    /// result — v1 emits all of those as <c>object</c> for uniform
    /// dispatch. We handle them in numeric contexts by coercing at
    /// runtime via <see cref="Convert.ToInt32(object)"/> /
    /// <see cref="Convert.ToInt64(object)"/> / <see
    /// cref="Convert.ToDouble(object)"/>.
    /// </summary>
    private static bool IsNumericOrObject(Type t) =>
        IsNumericType(t) || t == typeof(object);

    private static Type? CommonNumericType(Type left, Type right)
    {
        if (!IsNumericOrObject(left) || !IsNumericOrObject(right)) return null;
        if (left == typeof(double) || right == typeof(double)) return typeof(double);
        if (left == typeof(long) || right == typeof(long)) return typeof(long);
        if (left == typeof(int) && right == typeof(int)) return typeof(int);
        // At least one operand is object — default to long, the
        // widest integer type that round-trips through Convert.ToInt64.
        return typeof(long);
    }

    private void ConvertNumeric(Type from, Type to)
    {
        if (from == to) return;
        if (from == typeof(object))
        {
            if (to == typeof(int)) _il.Emit(OpCodes.Call, s_convertToInt32);
            else if (to == typeof(long)) _il.Emit(OpCodes.Call, s_convertToInt64);
            else if (to == typeof(double)) _il.Emit(OpCodes.Call, s_convertToDouble);
            return;
        }
        if (to == typeof(double)) _il.Emit(OpCodes.Conv_R8);
        else if (to == typeof(long)) _il.Emit(OpCodes.Conv_I8);
        else if (to == typeof(int)) _il.Emit(OpCodes.Conv_I4);
    }

    private void BoxIfValueType(Type t)
    {
        if (t.IsValueType) _il.Emit(OpCodes.Box, t);
    }

    private void ConvertToString(Type t)
    {
        if (t == typeof(string)) return;
        if (t.IsValueType) _il.Emit(OpCodes.Box, t);
        _il.Emit(OpCodes.Callvirt, s_objectToString);
    }

}
