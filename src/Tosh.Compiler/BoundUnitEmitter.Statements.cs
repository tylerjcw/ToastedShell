using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Diagnostics.SymbolStore;
using Tosh.Language.Binding;
using Tosh.Language.Binding.BoundNodes;
using Tosh.Language.Parsing;
using Tosh.Runtime;
namespace Tosh.Compiler;

internal sealed partial class EmitterImpl
{
    private void EmitStatement(BoundStatement statement)
    {
        // Statement-granularity sequence point: a debugger will
        // single-step from one tosh statement to the next, and stack
        // traces will name the line containing the active statement.
        // Pipeline / variable / control-flow statements all carry a
        // span — synthetic statements without a span are handled by
        // MarkSeqPoint's empty-span guard.
        MarkSeqPoint(statement.Span);
        switch (statement)
        {
            case BoundPipelineStatement pipelineStmt:
                if (_blockOutputLocal is not null)
                {
                    // Lambda body context: collect output into _blockOutputLocal
                    // rather than printing to stdout.
                    EmitLambdaBodyPipelineStatement(pipelineStmt);
                }
                else if (_suppressStatementOutputDepth > 0)
                {
                    var suppressed = EmitPipeline(pipelineStmt.Pipeline, asStatement: false);
                    if (suppressed is not null)
                    {
                        _il.Emit(OpCodes.Pop);
                    }
                }
                else
                {
                    EmitPipeline(pipelineStmt.Pipeline, asStatement: true);
                }
                break;

            case BoundVariableDeclaration decl:
                EmitVariableDeclaration(decl);
                break;

            case BoundVariableAssignment assign:
                EmitVariableAssignment(assign);
                break;

            case BoundMemberAssignment memberAssign:
                EmitMemberAssignment(memberAssign);
                break;

            case BoundDestructuringDeclaration destructuring:
                EmitDestructuringDeclaration(destructuring);
                break;

            case BoundIfStatement ifStmt:
                EmitIfStatement(ifStmt);
                break;

            case BoundWhileStatement whileStmt:
                EmitWhileStatement(whileStmt);
                break;

            case BoundForStatement forStmt:
                EmitForStatement(forStmt);
                break;

            case BoundReturnStatement ret:
                EmitReturnStatement(ret);
                break;

            case BoundBreakStatement:
                if (_loopStack.Count == 0)
                {
                    Diagnostics.Add("'break' outside of a loop");
                    break;
                }
                _il.Emit(OpCodes.Leave, _loopStack.Peek().BreakLabel);
                break;

            case BoundContinueStatement:
                if (_loopStack.Count == 0)
                {
                    Diagnostics.Add("'continue' outside of a loop");
                    break;
                }
                _il.Emit(OpCodes.Leave, _loopStack.Peek().ContinueLabel);
                break;

            case BoundThrowStatement throwStmt:
                EmitThrowStatement(throwStmt);
                break;

            case BoundTryStatement tryStmt:
                EmitTryStatement(tryStmt);
                break;

            case BoundSwitchStatement switchStmt:
                EmitSwitchStatement(switchStmt);
                break;

            case BoundDeferStatement:
                // `defer` is lowered by EmitBlock into nested try/finally
                // wrappers around the remaining statements in the block.
                break;

            case BoundYieldStatement yieldStmt:
                EmitYieldStatement(yieldStmt);
                break;

            case BoundFunctionDefinition:
                // Nested function definitions are not yet supported.
                // Top-level ones are handled by Run() before reaching
                // this switch.
                Diagnostics.Add("nested function definitions are not supported");
                break;

            case BoundUsingStatement:
                // `using` affects binder/type resolution and runtime import
                // tables, but has no direct IL side effects in compiled mode.
                break;

            case BoundTupleAssignment tupleAssign:
                EmitTupleAssignment(tupleAssign);
                break;

            case BoundAllocStatement allocStmt:
                Diagnostics.Add(
                    $"compiled tosh: `alloc {allocStmt.Name} = ...` (native interop allocation) "
                    + "is not yet supported by the IL backend; use the interpreter or "
                    + "drop to a manual ToshHost.Alloc call.");
                break;

            default:
                Diagnostics.Add($"unsupported statement: {statement.GetType().Name}");
                break;
        }
    }

    /// <summary>
    /// Emits IL for <c>($a, $b) = pipeline</c>. Evaluates the RHS
    /// pipeline as a value, then for each named target on the left
    /// looks up the existing local by symbol name (the lowerer does
    /// not resolve <see cref="BoundTupleAssignment.Names"/> to
    /// <see cref="BoundSymbol"/>s today, so we fall back to a
    /// name-based scan over the active local table). Assigns the
    /// i-th element of the iterable RHS to the i-th name.
    /// </summary>
    private void EmitTupleAssignment(BoundTupleAssignment tupleAssign)
    {
        // Evaluate RHS to object, store in local.
        var rhsType = EmitPipelineAsValue(tupleAssign.Value);
        if (rhsType is null) return;
        BoxIfValueType(rhsType);

        var rhsLocal = _il.DeclareLocal(typeof(object));
        _il.Emit(OpCodes.Stloc, rhsLocal);

        // Materialize iterable -> object[] via host helper.
        _il.Emit(OpCodes.Ldloc, rhsLocal);
        _il.Emit(OpCodes.Call, s_hostToArray);
        var arrLocal = _il.DeclareLocal(typeof(object[]));
        _il.Emit(OpCodes.Stloc, arrLocal);

        for (var i = 0; i < tupleAssign.Names.Count; i++)
        {
            var name = tupleAssign.Names[i];
            LocalSlot? slot = null;
            foreach (var kv in _locals)
            {
                if (string.Equals(kv.Key.Name, name, StringComparison.Ordinal))
                {
                    slot = kv.Value;
                    break;
                }
            }
            if (slot is null)
            {
                Diagnostics.Add(
                    $"tuple assignment: target variable '${name}' is not a "
                    + "local in scope (declare it first with `var`).");
                return;
            }

            // value = arr[i]  (with bounds-fallback to null)
            _il.Emit(OpCodes.Ldloc, arrLocal);
            _il.Emit(OpCodes.Ldc_I4, i);
            _il.Emit(OpCodes.Call, s_hostIndexOrNull);
            // Stack: object value
            EmitStoreToLocalSlot(slot.Value);
        }
    }

    /// <summary>
    /// Stores the boxed object on the IL stack into
    /// <paramref name="slot"/>, unboxing/converting where
    /// necessary to match the slot's static type.
    /// </summary>
    private void EmitStoreToLocalSlot(LocalSlot slot)
    {
        var slotType = slot.Type;
        if (slotType.IsValueType)
        {
            _il.Emit(OpCodes.Unbox_Any, slotType);
        }
        else if (slotType != typeof(object))
        {
            _il.Emit(OpCodes.Castclass, slotType);
        }
        _il.Emit(OpCodes.Stloc, slot.Local);
    }

    private void EmitLambdaBodyPipelineStatement(BoundPipelineStatement pipelineStmt)
    {
        if (EmitBlockBodyPipelineStatement(pipelineStmt)) return;

        var suppressed = EmitPipeline(pipelineStmt.Pipeline, asStatement: false);
        if (suppressed is not null)
        {
            _il.Emit(OpCodes.Pop);
        }
    }

    private void EmitVariableDeclaration(BoundVariableDeclaration decl)
    {
        // Captured top-level symbols live in a static field rather
        // than a method local so nested functions see by-reference
        // semantics.
        if (_staticFields.TryGetValue(decl.Symbol, out var captureField))
        {
            if (decl.Value is null)
            {
                _il.Emit(OpCodes.Ldnull);
            }
            else
            {
                var produced = EmitPipelineAsValue(decl.Value);
                if (produced is null)
                {
                    _il.Emit(OpCodes.Ldnull);
                }
                else
                {
                    BoxIfValueType(produced);
                }
            }
            _il.Emit(OpCodes.Stsfld, captureField);
            return;
        }

        if (decl.Value is null)
        {
            var slot = _il.DeclareLocal(typeof(object));
            _il.Emit(OpCodes.Ldnull);
            _il.Emit(OpCodes.Stloc, slot);
            _locals[decl.Symbol] = new LocalSlot(slot, typeof(object));
            return;
        }

        var producedType = EmitPipelineAsValue(decl.Value);
        if (producedType is null)
        {
            // Diagnostic was already recorded; still need a slot so
            // later refs don't crash. Default to object/null.
            producedType = typeof(object);
            _il.Emit(OpCodes.Ldnull);
        }
        // Refinement-check enforcement: when the declared symbol
        // type is a refinement, route the value through
        // ToshHost.CheckType so the IL throws a runtime diagnostic
        // on annotation failure, matching the interpreter's
        // semantics. The check leaves an `object` on the stack and
        // promotes the local's storage type accordingly.
        if (decl.Symbol.DeclaredType is RefinementType refDecl)
        {
            BoxIfValueType(producedType);
            _il.Emit(OpCodes.Ldstr, refDecl.Name);
            _il.Emit(OpCodes.Ldc_I4, decl.Span.Start);
            _il.Emit(OpCodes.Ldc_I4, decl.Span.Length);
            _il.Emit(OpCodes.Ldstr, $"var {decl.Symbol.Name}");
            _il.Emit(OpCodes.Call, s_hostCheckType);
            producedType = typeof(object);
        }
        var local = _il.DeclareLocal(producedType);
        _il.Emit(OpCodes.Stloc, local);
        _locals[decl.Symbol] = new LocalSlot(local, producedType);
    }

    /// <summary>
    /// Emits a reassignment <c>$x = ...</c>. Currently supports plain
    /// <c>=</c> on a previously-declared local whose stored type
    /// matches (or can be implicitly converted from) the new value,
    /// plus the compound forms <c>+= -= *= /= %=</c>. The compound
    /// forms are lowered to <c>$x = $x op rhs</c> at IL time, sharing
    /// the numeric-coercion path with <see cref="EmitNumericArith"/>.
    /// String <c>+=</c> falls into the string-concat branch.
    /// </summary>
    private void EmitVariableAssignment(BoundVariableAssignment assign)
    {
        if (assign.Symbol is not null && (_paramSlots.ContainsKey(assign.Symbol) || _typedParamLocals.ContainsKey(assign.Symbol)))
        {
            Diagnostics.Add($"cannot reassign parameter '{assign.Name}'");
            return;
        }
        if (assign.Symbol is not null && _staticFields.TryGetValue(assign.Symbol, out var captureField))
        {
            EmitCaptureFieldAssignment(captureField, assign);
            return;
        }
        if (assign.Symbol is null || !_locals.TryGetValue(assign.Symbol, out var slot))
        {
            Diagnostics.Add($"unresolved assignment target: {assign.Name}");
            return;
        }

        var op = assign.Operator;
        if (op == "=")
        {
            EmitPlainAssignmentInto(slot, assign);
            return;
        }

        // Compound assignment: load current value, emit RHS, combine,
        // store. Mirrors EmitBinaryOperator's coercion rules.
        var binaryOp = op switch
        {
            "+=" => "+",
            "-=" => "-",
            "*=" => "*",
            "/=" => "/",
            "%=" => "%",
            _ => null,
        };
        if (binaryOp is null)
        {
            Diagnostics.Add($"unsupported assignment operator: '{op}'");
            return;
        }

        _il.Emit(OpCodes.Ldloc, slot.Local);
        var leftType = slot.Type;

        // String += rhs → string.Concat(left, rhs.ToString()).
        if (binaryOp == "+" && leftType == typeof(string))
        {
            var rhsStrType = EmitPipelineAsValue(assign.Value);
            if (rhsStrType is null) { _il.Emit(OpCodes.Pop); return; }
            ConvertToString(rhsStrType);
            _il.Emit(OpCodes.Call, typeof(string).GetMethod(
                nameof(string.Concat), new[] { typeof(string), typeof(string) })!);
            _il.Emit(OpCodes.Stloc, slot.Local);
            return;
        }

        var rhsType = EmitPipelineAsValue(assign.Value);
        if (rhsType is null) { _il.Emit(OpCodes.Pop); return; }
        var resultType = EmitNumericArith(binaryOp, leftType, rhsType);
        if (resultType is null) return;
        if (resultType != slot.Type)
        {
            ConvertNumeric(resultType, slot.Type);
        }
        _il.Emit(OpCodes.Stloc, slot.Local);
    }

    private void EmitPlainAssignmentInto(LocalSlot slot, BoundVariableAssignment assign)
    {
        var producedType = EmitPipelineAsValue(assign.Value);
        if (producedType is null) return;

        // Refinement enforcement on reassignment: when the target
        // symbol declared a refinement type, route the new value
        // through ToshHost.CheckType before storing. Promotes the
        // value to object, so we then re-coerce to the slot type
        // just like any other assignment shape.
        if (assign.Symbol is not null && assign.Symbol.DeclaredType is RefinementType refSym)
        {
            BoxIfValueType(producedType);
            _il.Emit(OpCodes.Ldstr, refSym.Name);
            _il.Emit(OpCodes.Ldc_I4, assign.Span.Start);
            _il.Emit(OpCodes.Ldc_I4, assign.Span.Length);
            _il.Emit(OpCodes.Ldstr, $"var {assign.Name}");
            _il.Emit(OpCodes.Call, s_hostCheckType);
            producedType = typeof(object);
        }

        if (producedType != slot.Type)
        {
            if (IsNumericType(producedType) && IsNumericType(slot.Type))
            {
                ConvertNumeric(producedType, slot.Type);
            }
            else if (slot.Type == typeof(object))
            {
                BoxIfValueType(producedType);
            }
            else
            {
                Diagnostics.Add(
                    $"assignment type mismatch for '{assign.Name}': " +
                    $"slot is {slot.Type.Name}, value is {producedType.Name}");
                _il.Emit(OpCodes.Pop);
                return;
            }
        }

        _il.Emit(OpCodes.Stloc, slot.Local);
    }

    /// <summary>
    /// Compound + plain assignment to a captured top-level symbol
    /// stored in a static field. The field is always typed
    /// <c>object</c>, so we coerce the right-hand side through the
    /// usual numeric / string / box paths and end with <c>Stsfld</c>.
    /// </summary>
    private void EmitCaptureFieldAssignment(FieldBuilder field, BoundVariableAssignment assign)
    {
        var op = assign.Operator;
        if (op == "=")
        {
            var produced = EmitPipelineAsValue(assign.Value);
            if (produced is null) return;
            BoxIfValueType(produced);
            // Refinement enforcement on captured-field reassignment.
            if (assign.Symbol is not null && assign.Symbol.DeclaredType is RefinementType refSym)
            {
                _il.Emit(OpCodes.Ldstr, refSym.Name);
                _il.Emit(OpCodes.Ldc_I4, assign.Span.Start);
                _il.Emit(OpCodes.Ldc_I4, assign.Span.Length);
                _il.Emit(OpCodes.Ldstr, $"var {assign.Name}");
                _il.Emit(OpCodes.Call, s_hostCheckType);
            }
            _il.Emit(OpCodes.Stsfld, field);
            return;
        }

        var binaryOp = op switch
        {
            "+=" => "+",
            "-=" => "-",
            "*=" => "*",
            "/=" => "/",
            "%=" => "%",
            _ => null,
        };
        if (binaryOp is null)
        {
            Diagnostics.Add($"unsupported assignment operator: '{op}'");
            return;
        }

        // Load current field value, run the op via the same numeric
        // dispatcher used by `+`/`-`/etc., then store back. Both
        // operands are object (boxed) so EmitNumericArith unboxes
        // through Convert.* — same path as a regular `$x + y`.
        _il.Emit(OpCodes.Ldsfld, field);
        var rhsType = EmitPipelineAsValue(assign.Value);
        if (rhsType is null) { _il.Emit(OpCodes.Pop); return; }
        BoxIfValueType(rhsType);
        var resultType = EmitNumericArith(binaryOp, typeof(object), typeof(object));
        if (resultType is null) return;
        BoxIfValueType(resultType);
        _il.Emit(OpCodes.Stsfld, field);
    }

    /// <summary>
    /// Emits assignment to a member/index target (for example
    /// <c>$obj.Name = x</c>, <c>$obj.Name += x</c>, or future indexed
    /// targets). Compound forms are lowered via
    /// <c>OperatorEvaluator.EvaluateBinary</c> to keep semantics aligned
    /// with the interpreter's operator dispatcher.
    /// </summary>
    private void EmitMemberAssignment(BoundMemberAssignment assign)
    {
        switch (assign.Target)
        {
            case BoundMemberAccess member:
                EmitMemberPathAssignment(member, assign);
                return;

            case BoundIndexAccess index:
                EmitIndexTargetAssignment(index, assign);
                return;

            default:
                Diagnostics.Add(
                    $"unsupported member assignment target: {assign.Target.GetType().Name}");
                return;
        }
    }

    private static string? GetCompoundAssignmentOperator(string assignmentOperator)
        => assignmentOperator switch
        {
            "+=" => "+",
            "-=" => "-",
            "*=" => "*",
            "/=" => "/",
            "%=" => "%",
            _ => null,
        };

    private void EmitMemberPathAssignment(BoundMemberAccess target, BoundMemberAssignment assign)
    {
        var targetType = EmitExpression(target.Target);
        if (targetType is null) return;
        BoxIfValueType(targetType);
        var targetLocal = _il.DeclareLocal(typeof(object));
        _il.Emit(OpCodes.Stloc, targetLocal);

        var valueLocal = _il.DeclareLocal(typeof(object));
        if (assign.Operator == "=")
        {
            var rhsType = EmitPipelineAsValue(assign.Value);
            if (rhsType is null) return;
            BoxIfValueType(rhsType);
            _il.Emit(OpCodes.Stloc, valueLocal);
        }
        else
        {
            var binaryOperator = GetCompoundAssignmentOperator(assign.Operator);
            if (binaryOperator is null)
            {
                Diagnostics.Add($"unsupported assignment operator: '{assign.Operator}'");
                return;
            }

            _il.Emit(OpCodes.Ldloc, targetLocal);
            _il.Emit(OpCodes.Ldstr, target.MemberPath);
            _il.Emit(target.NullSafe ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
            _il.Emit(OpCodes.Call, s_hostGetMember);
            _il.Emit(OpCodes.Ldstr, binaryOperator);

            var rhsType = EmitPipelineAsValue(assign.Value);
            if (rhsType is null) return;
            BoxIfValueType(rhsType);

            _il.Emit(OpCodes.Call, s_opEvaluateBinary);
            _il.Emit(OpCodes.Stloc, valueLocal);
        }

        _il.Emit(OpCodes.Ldloc, targetLocal);
        _il.Emit(OpCodes.Ldstr, target.MemberPath);
        _il.Emit(OpCodes.Ldloc, valueLocal);
        _il.Emit(target.NullSafe ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
        _il.Emit(OpCodes.Call, s_hostSetMember);
        _il.Emit(OpCodes.Pop);
    }

    private void EmitIndexTargetAssignment(BoundIndexAccess target, BoundMemberAssignment assign)
    {
        if (target.LookupKind != global::Tosh.Runtime.IndexLookupKind.Default)
        {
            Diagnostics.Add(
                $"index assignment lookup kind '{target.LookupKind}' not yet supported");
            return;
        }

        var targetType = EmitExpression(target.Target);
        if (targetType is null) return;
        BoxIfValueType(targetType);
        var targetLocal = _il.DeclareLocal(typeof(object));
        _il.Emit(OpCodes.Stloc, targetLocal);

        var indexType = EmitExpression(target.Index);
        if (indexType is null) return;
        BoxIfValueType(indexType);
        var indexLocal = _il.DeclareLocal(typeof(object));
        _il.Emit(OpCodes.Stloc, indexLocal);

        var valueLocal = _il.DeclareLocal(typeof(object));
        if (assign.Operator == "=")
        {
            var rhsType = EmitPipelineAsValue(assign.Value);
            if (rhsType is null) return;
            BoxIfValueType(rhsType);
            _il.Emit(OpCodes.Stloc, valueLocal);
        }
        else
        {
            var binaryOperator = GetCompoundAssignmentOperator(assign.Operator);
            if (binaryOperator is null)
            {
                Diagnostics.Add($"unsupported assignment operator: '{assign.Operator}'");
                return;
            }

            _il.Emit(OpCodes.Ldloc, targetLocal);
            _il.Emit(OpCodes.Ldloc, indexLocal);
            _il.Emit(OpCodes.Call, s_hostGetIndex);
            _il.Emit(OpCodes.Ldstr, binaryOperator);

            var rhsType = EmitPipelineAsValue(assign.Value);
            if (rhsType is null) return;
            BoxIfValueType(rhsType);

            _il.Emit(OpCodes.Call, s_opEvaluateBinary);
            _il.Emit(OpCodes.Stloc, valueLocal);
        }

        _il.Emit(OpCodes.Ldloc, targetLocal);
        _il.Emit(OpCodes.Ldloc, indexLocal);
        _il.Emit(OpCodes.Ldloc, valueLocal);
        _il.Emit(OpCodes.Call, s_hostSetIndex);
        _il.Emit(OpCodes.Pop);
    }

    /// <summary>
    /// Emits destructuring declaration binding for array/record patterns.
    /// The RHS pipeline is evaluated exactly once and then split through
    /// host helpers that mirror interpreter semantics.
    /// </summary>
    private void EmitDestructuringDeclaration(BoundDestructuringDeclaration destructuring)
    {
        var produced = EmitPipelineAsValue(destructuring.Value);
        if (produced is null)
        {
            _il.Emit(OpCodes.Ldnull);
            produced = typeof(object);
        }
        BoxIfValueType(produced);
        var valueLocal = _il.DeclareLocal(typeof(object));
        _il.Emit(OpCodes.Stloc, valueLocal);

        switch (destructuring.Pattern)
        {
            case BoundArrayDestructuringPattern arrayPattern:
                EmitArrayDestructuringBindings(arrayPattern.Symbols, valueLocal);
                return;

            case BoundRecordDestructuringPattern recordPattern:
                EmitRecordDestructuringBindings(recordPattern.Symbols, valueLocal);
                return;

            default:
                Diagnostics.Add(
                    $"unsupported destructuring pattern: {destructuring.Pattern.GetType().Name}");
                return;
        }
    }

    private void EmitArrayDestructuringBindings(
        IReadOnlyList<BoundSymbol> symbols,
        LocalBuilder valueLocal)
    {
        _il.Emit(OpCodes.Ldloc, valueLocal);
        _il.Emit(OpCodes.Ldc_I4, symbols.Count);
        _il.Emit(OpCodes.Call, s_hostDestructureArray);
        var valuesLocal = _il.DeclareLocal(typeof(object[]));
        _il.Emit(OpCodes.Stloc, valuesLocal);

        for (var i = 0; i < symbols.Count; i++)
        {
            _il.Emit(OpCodes.Ldloc, valuesLocal);
            _il.Emit(OpCodes.Ldc_I4, i);
            _il.Emit(OpCodes.Ldelem_Ref);
            StoreDestructuredSymbol(symbols[i]);
        }
    }

    private void EmitRecordDestructuringBindings(
        IReadOnlyList<BoundSymbol> symbols,
        LocalBuilder valueLocal)
    {
        _il.Emit(OpCodes.Ldloc, valueLocal);
        _il.Emit(OpCodes.Ldc_I4, symbols.Count);
        _il.Emit(OpCodes.Newarr, typeof(string));
        for (var i = 0; i < symbols.Count; i++)
        {
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldc_I4, i);
            _il.Emit(OpCodes.Ldstr, symbols[i].Name);
            _il.Emit(OpCodes.Stelem_Ref);
        }
        _il.Emit(OpCodes.Call, s_hostDestructureRecord);
        var valuesLocal = _il.DeclareLocal(typeof(object[]));
        _il.Emit(OpCodes.Stloc, valuesLocal);

        for (var i = 0; i < symbols.Count; i++)
        {
            _il.Emit(OpCodes.Ldloc, valuesLocal);
            _il.Emit(OpCodes.Ldc_I4, i);
            _il.Emit(OpCodes.Ldelem_Ref);
            StoreDestructuredSymbol(symbols[i]);
        }
    }

    private void StoreDestructuredSymbol(BoundSymbol symbol)
    {
        if (_staticFields.TryGetValue(symbol, out var captureField))
        {
            _il.Emit(OpCodes.Stsfld, captureField);
            return;
        }

        var local = _il.DeclareLocal(typeof(object));
        _il.Emit(OpCodes.Stloc, local);
        _locals[symbol] = new LocalSlot(local, typeof(object));
    }

    /// <summary>
    /// Emits an <c>if cond { … } else { … }</c>. The condition must
    /// evaluate to <see cref="bool"/>; nested non-bool conditions are
    /// reported as a diagnostic and the block is skipped.
    /// </summary>
    private void EmitIfStatement(BoundIfStatement ifStmt)
    {
        var condType = EmitExpression(ifStmt.Condition);
        if (condType is null) return;
        if (condType != typeof(bool))
        {
            Diagnostics.Add($"if condition must be bool, got {condType.Name}");
            _il.Emit(OpCodes.Pop);
            return;
        }

        var elseLabel = _il.DefineLabel();
        var endLabel = _il.DefineLabel();

        _il.Emit(OpCodes.Brfalse, elseLabel);
        EmitBlock(ifStmt.ThenBlock);
        _il.Emit(OpCodes.Br, endLabel);

        _il.MarkLabel(elseLabel);
        if (ifStmt.ElseBlock is not null)
        {
            EmitBlock(ifStmt.ElseBlock);
        }
        _il.MarkLabel(endLabel);
    }

    /// <summary>
    /// Emits a <c>while cond { … }</c> or <c>until cond { … }</c>
    /// loop. The two forms differ only in which branch opcode tests
    /// the condition (<c>brfalse</c> vs <c>brtrue</c>).
    /// </summary>
    private void EmitWhileStatement(BoundWhileStatement whileStmt)
    {
        var topLabel = _il.DefineLabel();
        var endLabel = _il.DefineLabel();

        _il.MarkLabel(topLabel);
        var condType = EmitExpression(whileStmt.Condition);
        if (condType is null) return;
        if (condType != typeof(bool))
        {
            Diagnostics.Add($"while condition must be bool, got {condType.Name}");
            _il.Emit(OpCodes.Pop);
            return;
        }

        // until inverts the test: keep looping while condition is true.
        _il.Emit(whileStmt.IsUntil ? OpCodes.Brtrue : OpCodes.Brfalse, endLabel);
        _loopStack.Push(new LoopFrame(ContinueLabel: topLabel, BreakLabel: endLabel));
        try
        {
            EmitBlock(whileStmt.Body);
        }
        finally
        {
            _loopStack.Pop();
        }
        _il.Emit(OpCodes.Br, topLabel);
        _il.MarkLabel(endLabel);
    }

    /// <summary>
    /// Emits a <c>for var in source { … }</c> loop. v1 only handles
    /// integer ranges with no explicit step:
    ///   for $i in start..end { … }
    /// where Start and End are evaluable to integers (or object that
    /// can be coerced via <see cref="Convert.ToInt32(object)"/>).
    /// Ranges are inclusive on both ends, matching
    /// <c>ToshRange.Enumerate</c>. Other source shapes (array
    /// literals, command pipelines, lazy sequences) record an
    /// unsupported diagnostic so the caller can fall back.
    /// </summary>
    private void EmitForStatement(BoundForStatement forStmt)
    {
        // Range fast path: `for i in (1..10)` → int counter loop.
        if (forStmt.Source.Stages.Count == 1 &&
            forStmt.Source.Stages[0] is BoundExpressionStage stage &&
            stage.Value is BoundRange range &&
            range.Step is null &&
            range.End is not null)
        {
            EmitForRangeStatement(forStmt, range);
            return;
        }

        // Generic fallback: evaluate the source as an object,
        // coerce via ToshHost.ToEnumerable, walk via IEnumerator.
        EmitForEachStatement(forStmt);
    }

    private void EmitForRangeStatement(BoundForStatement forStmt, BoundRange range)
    {
        var startType = EmitExpression(range.Start);
        if (startType is null) return;
        ConvertNumeric(startType, typeof(int));
        var loopVarLocal = _il.DeclareLocal(typeof(int));
        _il.Emit(OpCodes.Stloc, loopVarLocal);
        _locals[forStmt.LoopVariable] = new LocalSlot(loopVarLocal, typeof(int));

        var endType = EmitExpression(range.End!);
        if (endType is null) return;
        ConvertNumeric(endType, typeof(int));
        var endLocal = _il.DeclareLocal(typeof(int));
        _il.Emit(OpCodes.Stloc, endLocal);

        var topLabelF = _il.DefineLabel();
        var endLabelF = _il.DefineLabel();
        var contLabelF = _il.DefineLabel();
        _il.MarkLabel(topLabelF);

        // Exit when loopVar > end (inclusive upper bound).
        _il.Emit(OpCodes.Ldloc, loopVarLocal);
        _il.Emit(OpCodes.Ldloc, endLocal);
        _il.Emit(OpCodes.Cgt);
        _il.Emit(OpCodes.Brtrue, endLabelF);

        _loopStack.Push(new LoopFrame(ContinueLabel: contLabelF, BreakLabel: endLabelF));
        try
        {
            EmitBlock(forStmt.Body);
        }
        finally
        {
            _loopStack.Pop();
        }

        // continue lands here, before the increment.
        _il.MarkLabel(contLabelF);
        // loopVar++
        _il.Emit(OpCodes.Ldloc, loopVarLocal);
        _il.Emit(OpCodes.Ldc_I4_1);
        _il.Emit(OpCodes.Add);
        _il.Emit(OpCodes.Stloc, loopVarLocal);
        _il.Emit(OpCodes.Br, topLabelF);
        _il.MarkLabel(endLabelF);
    }

    /// <summary>
    /// Generic <c>for x in expr</c>: evaluates the source as an
    /// object, calls <see cref="global::Tosh.Compiler.Runtime.ToshHost.ToEnumerable"/>
    /// to coerce it into <c>IEnumerable&lt;object?&gt;</c>, then
    /// walks via <c>GetEnumerator</c>/<c>MoveNext</c>/<c>Current</c>
    /// inside a try/finally that disposes the enumerator.
    /// </summary>
    private void EmitForEachStatement(BoundForStatement forStmt)
    {
        // Evaluate the source pipeline as a value.
        var srcType = EmitPipelineAsValue(forStmt.Source);
        if (srcType is null) return;
        BoxIfValueType(srcType);
        _il.Emit(OpCodes.Call, s_hostToEnumerable);

        // Get an IEnumerator<object?> from the IEnumerable<object?>.
        _il.Emit(OpCodes.Callvirt, s_enumerableGetEnumerator);
        var enumeratorLocal = _il.DeclareLocal(typeof(IEnumerator<object?>));
        _il.Emit(OpCodes.Stloc, enumeratorLocal);

        // Loop variable is object-typed in the generic case.
        var loopVarLocal = _il.DeclareLocal(typeof(object));
        _locals[forStmt.LoopVariable] = new LocalSlot(loopVarLocal, typeof(object));

        var afterLoopLabel = _il.DefineLabel();
        _il.BeginExceptionBlock();
        var topLabelF = _il.DefineLabel();
        var endLabelF = _il.DefineLabel();
        _il.MarkLabel(topLabelF);

        // if (!enumerator.MoveNext()) goto end
        _il.Emit(OpCodes.Ldloc, enumeratorLocal);
        _il.Emit(OpCodes.Callvirt, s_enumeratorMoveNext);
        _il.Emit(OpCodes.Brfalse, endLabelF);

        // loopVar = enumerator.Current
        _il.Emit(OpCodes.Ldloc, enumeratorLocal);
        _il.Emit(OpCodes.Callvirt, s_enumeratorOfObjectGetCurrent);
        _il.Emit(OpCodes.Stloc, loopVarLocal);

        // break exits the foreach (running finally to dispose);
        // continue heads back to the MoveNext check at topLabelF.
        _loopStack.Push(new LoopFrame(ContinueLabel: topLabelF, BreakLabel: afterLoopLabel));
        try
        {
            EmitBlock(forStmt.Body);
        }
        finally
        {
            _loopStack.Pop();
        }
        _il.Emit(OpCodes.Br, topLabelF);

        _il.MarkLabel(endLabelF);
        _il.Emit(OpCodes.Leave, afterLoopLabel);

        _il.BeginFinallyBlock();
        // enumerator?.Dispose();
        var skipDispose = _il.DefineLabel();
        _il.Emit(OpCodes.Ldloc, enumeratorLocal);
        _il.Emit(OpCodes.Brfalse_S, skipDispose);
        _il.Emit(OpCodes.Ldloc, enumeratorLocal);
        _il.Emit(OpCodes.Callvirt, s_disposableDispose);
        _il.MarkLabel(skipDispose);
        _il.EndExceptionBlock();
        _il.MarkLabel(afterLoopLabel);
    }

    private void EmitReturnStatement(BoundReturnStatement ret)
    {
        // Lambda body context: add return value to output list, then return the list.
        if (_blockOutputLocal is not null)
        {
            if (ret.Value is not null)
            {
                var retType = EmitPipelineAsValue(ret.Value);
                if (retType is not null)
                {
                    BoxIfValueType(retType);
                    var tmp = _il.DeclareLocal(typeof(object));
                    _il.Emit(OpCodes.Stloc, tmp);
                    var skipAdd = _il.DefineLabel();
                    _il.Emit(OpCodes.Ldloc, tmp);
                    _il.Emit(OpCodes.Brfalse_S, skipAdd);
                    _il.Emit(OpCodes.Ldloc, _blockOutputLocal);
                    _il.Emit(OpCodes.Ldloc, tmp);
                    _il.Emit(OpCodes.Callvirt, s_listAdd);
                    _il.MarkLabel(skipAdd);
                }
            }
            _il.Emit(OpCodes.Ldloc, _blockOutputLocal);
            _il.Emit(OpCodes.Ret);
            return;
        }

        var typedReturn = _currentFunctionReturnType;
        if (ret.Value is null)
        {
            if (typedReturn is not null)
            {
                EmitDefaultValueForType(typedReturn);
            }
            else
            {
                _il.Emit(OpCodes.Ldnull);
            }
            _il.Emit(OpCodes.Ret);
            return;
        }
        var t = EmitPipelineAsValue(ret.Value);
        if (t is null)
        {
            if (typedReturn is not null)
            {
                EmitDefaultValueForType(typedReturn);
            }
            else
            {
                _il.Emit(OpCodes.Ldnull);
            }
            _il.Emit(OpCodes.Ret);
            return;
        }
        if (typedReturn is not null)
        {
            // Refinement enforcement on return value: route the
            // (boxed) result through ToshHost.CheckType. Throws
            // tosh.runtime.annotation_conversion_failed when the
            // returned value violates the declared refinement.
            // Performed before numeric coercion so the host sees
            // the raw runtime value the user produced.
            if (_currentFunctionReturnRefinement is RefinementType refRet)
            {
                BoxIfValueType(t);
                _il.Emit(OpCodes.Ldstr, refRet.Name);
                _il.Emit(OpCodes.Ldc_I4, ret.Span.Start);
                _il.Emit(OpCodes.Ldc_I4, ret.Span.Length);
                _il.Emit(OpCodes.Ldstr, "return value");
                _il.Emit(OpCodes.Call, s_hostCheckType);
                t = typeof(object);
            }
            // Coerce expression CLR type → declared return type.
            // Numeric returns: stay unboxed, use ConvertNumeric for
            // primitive widening (e.g. arithmetic widens to long but
            // declared return is `int`). Other shapes: box to object
            // and round-trip through Convert.ChangeType / castclass.
            if (typedReturn.IsValueType && IsNumericType(typedReturn) && IsNumericOrObject(t))
            {
                ConvertNumeric(t, typedReturn);
            }
            else if (typedReturn != t)
            {
                if (t.IsValueType) _il.Emit(OpCodes.Box, t);
                CoerceObjectToTyped(_il, typedReturn);
            }
        }
        else
        {
            BoxIfValueType(t);
        }
        _il.Emit(OpCodes.Ret);
    }

    private void EmitBlock(BoundBlock block)
    {
        EmitBlockStatementsWithDefers(block.Statements, 0);
    }

    private void EmitBlockStatementsWithDefers(IReadOnlyList<BoundStatement> statements, int index)
    {
        if (index >= statements.Count)
        {
            return;
        }

        if (statements[index] is BoundDeferStatement defer)
        {
            _il.BeginExceptionBlock();
            EmitBlockStatementsWithDefers(statements, index + 1);
            _il.BeginFinallyBlock();
            EmitDeferredBlock(defer.Body);
            _il.EndExceptionBlock();
            return;
        }

        EmitStatement(statements[index]);
        EmitBlockStatementsWithDefers(statements, index + 1);
    }

    private void EmitDeferredBlock(BoundBlock body)
    {
        _suppressStatementOutputDepth++;
        try
        {
            EmitBlock(body);
        }
        finally
        {
            _suppressStatementOutputDepth--;
        }
    }

    private void EmitYieldStatement(BoundYieldStatement yieldStmt)
    {
        if (yieldStmt.Value is null)
        {
            return;
        }

        // In deferred blocks, yield output is suppressed just like
        // other statement output.
        if (_suppressStatementOutputDepth > 0)
        {
            var suppressed = EmitPipeline(yieldStmt.Value, asStatement: false);
            if (suppressed is not null)
            {
                _il.Emit(OpCodes.Pop);
            }
            return;
        }

        // Yielding a plain expression should surface the value, not
        // be dropped as a regular expression statement would be.
        if (yieldStmt.Value.Stages.Count == 1 &&
            yieldStmt.Value.Stages[0] is BoundExpressionStage exprStage)
        {
            var t = EmitExpression(exprStage.Value);
            if (t is null) return;
            BoxIfValueType(t);
            _il.Emit(OpCodes.Call, s_writeLineObject);
            return;
        }

        // Command/pipeline yields reuse statement-context pipeline
        // dispatch so command outputs flow to the active sink.
        EmitPipeline(yieldStmt.Value, asStatement: true);
    }

    /// <summary>
    /// <c>throw expr</c>: evaluate the value pipeline, box it, and
    /// hand it to <see cref="global::Tosh.Compiler.Runtime.ToshHost.ThrowValue"/>
    /// which raises a <see cref="global::Tosh.Runtime.ThrowSignalException"/>.
    /// A bare <c>throw</c> with no value re-throws the message-only
    /// default, matching the interpreter.
    /// </summary>
    private void EmitThrowStatement(BoundThrowStatement throwStmt)
    {
        if (throwStmt.Value is null)
        {
            _il.Emit(OpCodes.Ldnull);
        }
        else
        {
            var t = EmitPipelineAsValue(throwStmt.Value);
            if (t is null)
            {
                _il.Emit(OpCodes.Ldnull);
            }
            else
            {
                BoxIfValueType(t);
            }
        }
        _il.Emit(OpCodes.Call, s_hostThrowValue);
    }

    /// <summary>
    /// <c>try { … } [catch [(name)] { … }] [finally { … }]</c>. The
    /// catch arm filters on <see cref="global::System.Exception"/>
    /// so user code can catch directly raised
    /// <see cref="global::Tosh.Runtime.ToshError"/>-derived types
    /// alongside the wrapper
    /// <see cref="global::Tosh.Runtime.ThrowSignalException"/>.
    /// <see cref="global::Tosh.Runtime.ToshHost.CaughtValueOf"/>
    /// rethrows control-flow signals so user catch blocks can't
    /// accidentally swallow Return/Break/Continue, and unwraps
    /// wrapper exceptions to the user's original payload.
    /// </summary>
    private void EmitTryStatement(BoundTryStatement tryStmt)
    {
        _il.BeginExceptionBlock();
        EmitBlock(tryStmt.TryBlock);

        if (tryStmt.Catch is { } catchClause)
        {
            _il.BeginCatchBlock(typeof(global::System.Exception));
            // The exception is on the eval stack. CaughtValueOf
            // rethrows control-flow signals and otherwise yields
            // either the wrapper's .Value or the exception itself.
            if (catchClause.Variable is { } sym)
            {
                _il.Emit(OpCodes.Call, s_hostCaughtValueOf);
                var slot = _il.DeclareLocal(typeof(object));
                _il.Emit(OpCodes.Stloc, slot);
                _locals[sym] = new LocalSlot(slot, typeof(object));
            }
            else
            {
                // Even when there's no catch variable we must
                // route through CaughtValueOf so control-flow
                // signals are rethrown rather than swallowed.
                _il.Emit(OpCodes.Call, s_hostCaughtValueOf);
                _il.Emit(OpCodes.Pop);
            }
            EmitBlock(catchClause.Body);
        }

        if (tryStmt.Finally is { } finallyBlock)
        {
            _il.BeginFinallyBlock();
            EmitBlock(finallyBlock);
        }

        _il.EndExceptionBlock();
    }

    // ─── match / switch ──────────────────────────────────────────

    /// <summary>
    /// Lowers <c>match $x { 1 =&gt; …; default =&gt; … }</c> to a
    /// chain of pattern tests + guards. The match value is
    /// evaluated once into a fresh local; each arm's pattern is
    /// dispatched by bound-IR shape:
    /// <list type="bullet">
    /// <item><see cref="BoundComparisonPattern"/> →
    /// <c>OperatorEvaluator.Matches(value, op, operand, false)</c>.</item>
    /// <item><see cref="BoundRange"/> →
    /// <c>value &gt;= start &amp;&amp; value &lt;= end</c> (open-ended
    /// upper bound matches anything ≥ start).</item>
    /// <item>Anything else → <c>OperatorEvaluator.AreEqual(value, pattern)</c>.</item>
    /// </list>
    /// Guards run after a successful pattern test with the match
    /// value bound to <c>_</c>. Match arms are required to be
    /// expression-shaped (single-pipeline body) to participate in
    /// expression context; richer block bodies fall back to a
    /// diagnostic for now.
    /// </summary>
    private Type? EmitMatchExpression(BoundMatchExpression match)
    {
        var valueType = EmitExpression(match.Value);
        if (valueType is null) return null;
        BoxIfValueType(valueType);
        var valueLocal = _il.DeclareLocal(typeof(object));
        _il.Emit(OpCodes.Stloc, valueLocal);

        var resultLocal = _il.DeclareLocal(typeof(object));
        _il.Emit(OpCodes.Ldnull);
        _il.Emit(OpCodes.Stloc, resultLocal);

        var endLabel = _il.DefineLabel();

        _underscoreStack.Push(valueLocal);
        try
        {
            foreach (var arm in match.Arms)
            {
                var nextArmLabel = _il.DefineLabel();

                if (!arm.IsWildcard)
                {
                    if (!EmitPatternTest(arm.Pattern!, valueLocal))
                        return null;
                    _il.Emit(OpCodes.Brfalse, nextArmLabel);
                }

                if (arm.Guard is not null)
                {
                    var guardType = EmitExpression(arm.Guard);
                    if (guardType is null) return null;
                    BoxIfValueType(guardType);
                    _il.Emit(OpCodes.Call, s_opToBoolean);
                    _il.Emit(OpCodes.Brfalse, nextArmLabel);
                }

                if (!EmitMatchArmBodyAsValue(arm, resultLocal))
                    return null;
                _il.Emit(OpCodes.Br, endLabel);

                _il.MarkLabel(nextArmLabel);
            }
        }
        finally
        {
            _underscoreStack.Pop();
        }

        _il.MarkLabel(endLabel);
        _il.Emit(OpCodes.Ldloc, resultLocal);
        return typeof(object);
    }

    /// <summary>
    /// Lowers <c>switch ($x) { case … { }; default { } }</c> to the
    /// same pattern-test chain as <see cref="EmitMatchExpression"/>,
    /// but each case body executes for side effects only — no
    /// result is materialized. The <c>default</c> block runs when
    /// no case matches.
    /// </summary>
    private void EmitSwitchStatement(BoundSwitchStatement switchStmt)
    {
        var valueType = EmitExpression(switchStmt.Value);
        if (valueType is null) return;
        BoxIfValueType(valueType);
        var valueLocal = _il.DeclareLocal(typeof(object));
        _il.Emit(OpCodes.Stloc, valueLocal);

        var endLabel = _il.DefineLabel();

        _underscoreStack.Push(valueLocal);
        try
        {
            foreach (var c in switchStmt.Cases)
            {
                var nextCaseLabel = _il.DefineLabel();

                if (!EmitPatternTest(c.Pattern, valueLocal)) return;
                _il.Emit(OpCodes.Brfalse, nextCaseLabel);

                if (c.Guard is not null)
                {
                    var guardType = EmitExpression(c.Guard);
                    if (guardType is null) return;
                    BoxIfValueType(guardType);
                    _il.Emit(OpCodes.Call, s_opToBoolean);
                    _il.Emit(OpCodes.Brfalse, nextCaseLabel);
                }

                EmitBlock(c.Body);
                _il.Emit(OpCodes.Br, endLabel);

                _il.MarkLabel(nextCaseLabel);
            }

            if (switchStmt.Default is { } def)
            {
                EmitBlock(def);
            }
        }
        finally
        {
            _underscoreStack.Pop();
        }

        _il.MarkLabel(endLabel);
    }

    /// <summary>
    /// Pushes a <see cref="bool"/> onto the eval stack indicating
    /// whether <paramref name="pattern"/> matches the value held in
    /// <paramref name="valueLocal"/>. Returns false (with a
    /// diagnostic recorded) for unsupported pattern shapes.
    /// </summary>
    private bool EmitPatternTest(BoundExpression pattern, LocalBuilder valueLocal)
    {
        switch (pattern)
        {
            case BoundComparisonPattern cmp:
                _il.Emit(OpCodes.Ldloc, valueLocal);
                _il.Emit(OpCodes.Ldstr, cmp.Operator);
                {
                    var t = EmitExpression(cmp.Operand);
                    if (t is null) return false;
                    BoxIfValueType(t);
                }
                _il.Emit(OpCodes.Ldc_I4_0);
                _il.Emit(OpCodes.Call, s_opMatches);
                return true;

            case BoundRange range:
                // value >= start
                _il.Emit(OpCodes.Ldloc, valueLocal);
                _il.Emit(OpCodes.Ldstr, ">=");
                {
                    var t = EmitExpression(range.Start);
                    if (t is null) return false;
                    BoxIfValueType(t);
                }
                _il.Emit(OpCodes.Ldc_I4_0);
                _il.Emit(OpCodes.Call, s_opMatches);

                if (range.End is not null)
                {
                    // && value <= end
                    var falseLabel = _il.DefineLabel();
                    var doneLabel = _il.DefineLabel();
                    _il.Emit(OpCodes.Brfalse, falseLabel);

                    _il.Emit(OpCodes.Ldloc, valueLocal);
                    _il.Emit(OpCodes.Ldstr, "<=");
                    {
                        var t = EmitExpression(range.End);
                        if (t is null) return false;
                        BoxIfValueType(t);
                    }
                    _il.Emit(OpCodes.Ldc_I4_0);
                    _il.Emit(OpCodes.Call, s_opMatches);
                    _il.Emit(OpCodes.Br, doneLabel);

                    _il.MarkLabel(falseLabel);
                    _il.Emit(OpCodes.Ldc_I4_0);
                    _il.MarkLabel(doneLabel);
                }
                return true;

            default:
                _il.Emit(OpCodes.Ldloc, valueLocal);
                {
                    var t = EmitExpression(pattern);
                    if (t is null) return false;
                    BoxIfValueType(t);
                }
                _il.Emit(OpCodes.Call, s_opAreEqual);
                return true;
        }
    }

    /// <summary>
    /// Emits a match-arm body in expression context: the body must
    /// be a <see cref="BoundBlock"/> wrapping a single
    /// <see cref="BoundPipelineStatement"/>; that pipeline's value
    /// becomes the arm's result and is stored into
    /// <paramref name="resultLocal"/>. Multi-statement arm bodies
    /// are not yet supported in value context.
    /// </summary>
    private bool EmitMatchArmBodyAsValue(BoundMatchArm arm, LocalBuilder resultLocal)
    {
        if (arm.Body.Statements.Count == 1
            && arm.Body.Statements[0] is BoundPipelineStatement pipeStmt)
        {
            var t = EmitPipelineAsValue(pipeStmt.Pipeline);
            if (t is null) return false;
            BoxIfValueType(t);
            _il.Emit(OpCodes.Stloc, resultLocal);
            return true;
        }
        Diagnostics.Add(
            "match arm: only single-pipeline expression bodies are "
            + "supported in value context");
        return false;
    }

    // ─── Pipelines ────────────────────────────────────────────────

}
