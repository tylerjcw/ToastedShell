using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Tosh.Language.Binding;
using Tosh.Language.Binding.BoundNodes;

namespace Tosh.Compiler;

/// <summary>
/// IL emitter for Tosh's bound IR. Walks <see cref="BoundUnit"/> and
/// produces a runnable .NET assembly. Coverage grows incrementally;
/// shapes that aren't yet handled are recorded as
/// <see cref="EmitResult.UnsupportedShapes"/> diagnostics so callers
/// can choose to fall back to the tree-walking evaluator for those
/// programs.
///
/// Currently supported:
/// • <c>BoundScript</c> with <c>BoundPipelineStatement</c> children
/// • <c>BoundCommandCall</c> named <c>echo</c> with literal/expression args
/// • <c>BoundLiteral</c> of int/long/double/bool/string/null
/// • <c>BoundVariableDeclaration</c> + <c>BoundVariableReference</c>
/// • <c>BoundBinaryOperator</c> on numeric/string operands (<c>+ - * / %</c>,
///   plus <c>== != &lt; &gt;</c>)
/// • <c>BoundUnaryOperator</c> for <c>-x</c> and <c>!x</c>
/// • <c>BoundExpressionStage</c> as a pipeline stage
/// </summary>
public static class BoundUnitEmitter
{
    public static EmitResult Emit(BoundUnit unit, string assemblyName, Stream output)
    {
        var emitter = new EmitterImpl(unit, assemblyName);
        emitter.Run();
        emitter.SerializeTo(output);
        return new EmitResult(emitter.Diagnostics);
    }
}

/// <summary>
/// Result of an emit pass. <see cref="UnsupportedShapes"/> is empty
/// on a clean emit.
/// </summary>
public sealed record EmitResult(IReadOnlyList<string> UnsupportedShapes)
{
    public bool IsClean => UnsupportedShapes.Count == 0;
}

internal sealed class EmitterImpl
{
    private readonly BoundUnit _unit;
    private readonly PersistedAssemblyBuilder _ab;
    private readonly TypeBuilder _program;
    private readonly MethodBuilder _main;
    private ILGenerator _il;
    private Dictionary<BoundSymbol, LocalSlot> _locals = new();
    private Dictionary<BoundSymbol, int> _paramSlots = new();
    private readonly Dictionary<string, UserFunction> _userFunctions = new(StringComparer.Ordinal);
    public List<string> Diagnostics { get; } = new();

    private static readonly MethodInfo s_writeLineString =
        typeof(Console).GetMethod(nameof(Console.WriteLine), new[] { typeof(string) })!;
    private static readonly MethodInfo s_writeLineObject =
        typeof(Console).GetMethod(nameof(Console.WriteLine), new[] { typeof(object) })!;
    private static readonly MethodInfo s_stringJoin =
        typeof(string).GetMethod(nameof(string.Join), new[] { typeof(string), typeof(string[]) })!;
    private static readonly MethodInfo s_objectToString =
        typeof(object).GetMethod(nameof(object.ToString), Type.EmptyTypes)!;
    private static readonly MethodInfo s_objectEquals =
        typeof(object).GetMethod(nameof(object.Equals), new[] { typeof(object), typeof(object) })!;
    private static readonly MethodInfo s_convertToInt32 =
        typeof(Convert).GetMethod(nameof(Convert.ToInt32), new[] { typeof(object) })!;
    private static readonly MethodInfo s_convertToInt64 =
        typeof(Convert).GetMethod(nameof(Convert.ToInt64), new[] { typeof(object) })!;
    private static readonly MethodInfo s_convertToDouble =
        typeof(Convert).GetMethod(nameof(Convert.ToDouble), new[] { typeof(object) })!;

    private static readonly Type s_toshHost = typeof(global::Tosh.Compiler.Runtime.ToshHost);
    private static readonly MethodInfo s_hostInitialize =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.Initialize),
            new[] { typeof(global::Tosh.Runtime.ToshRuntime) })!;
    private static readonly MethodInfo s_hostInvokeStatement =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.InvokeStatement),
            new[] { typeof(string), typeof(object[]) })!;
    private static readonly MethodInfo s_hostInvokeValue =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.InvokeValue),
            new[] { typeof(string), typeof(object[]) })!;
    private static readonly MethodInfo s_hostGetMember =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.GetMember),
            new[] { typeof(object), typeof(string), typeof(bool) })!;
    private static readonly MethodInfo s_hostGetIndex =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.GetIndex),
            new[] { typeof(object), typeof(object) })!;
    private static readonly MethodInfo s_hostToEnumerable =
        s_toshHost.GetMethod(nameof(global::Tosh.Compiler.Runtime.ToshHost.ToEnumerable),
            new[] { typeof(object) })!;

    private static readonly Type s_listOfObject = typeof(List<object?>);
    private static readonly ConstructorInfo s_listCtor =
        s_listOfObject.GetConstructor(Type.EmptyTypes)!;
    private static readonly MethodInfo s_listAdd =
        s_listOfObject.GetMethod(nameof(List<object?>.Add))!;

    private static readonly Type s_dictOfStringObject = typeof(Dictionary<string, object?>);
    private static readonly ConstructorInfo s_dictCtor =
        s_dictOfStringObject.GetConstructor(Type.EmptyTypes)!;
    private static readonly MethodInfo s_dictSetItem =
        s_dictOfStringObject.GetMethod("set_Item", new[] { typeof(string), typeof(object) })!;

    private static readonly Type s_dictOfObjectObject = typeof(Dictionary<object, object?>);
    private static readonly ConstructorInfo s_dictObjCtor =
        s_dictOfObjectObject.GetConstructor(Type.EmptyTypes)!;
    private static readonly MethodInfo s_dictObjSetItem =
        s_dictOfObjectObject.GetMethod("set_Item", new[] { typeof(object), typeof(object) })!;

    private static readonly Type s_enumerableOfObject = typeof(IEnumerable<object?>);
    private static readonly MethodInfo s_enumerableGetEnumerator =
        s_enumerableOfObject.GetMethod(nameof(IEnumerable<object?>.GetEnumerator), Type.EmptyTypes)!;
    private static readonly MethodInfo s_enumeratorMoveNext =
        typeof(System.Collections.IEnumerator).GetMethod(nameof(System.Collections.IEnumerator.MoveNext), Type.EmptyTypes)!;
    private static readonly MethodInfo s_enumeratorOfObjectGetCurrent =
        typeof(IEnumerator<object?>).GetProperty(nameof(IEnumerator<object?>.Current))!.GetGetMethod()!;
    private static readonly MethodInfo s_disposableDispose =
        typeof(IDisposable).GetMethod(nameof(IDisposable.Dispose), Type.EmptyTypes)!;

    public EmitterImpl(BoundUnit unit, string assemblyName)
    {
        _unit = unit;
        var coreAssembly = typeof(object).Assembly;
        _ab = new PersistedAssemblyBuilder(new AssemblyName(assemblyName), coreAssembly);
        var module = _ab.DefineDynamicModule("MainModule");
        _program = module.DefineType(
            $"{assemblyName}.Program",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Abstract);

        _main = _program.DefineMethod(
            "Main",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(void),
            new[] { typeof(string[]) });

        _il = _main.GetILGenerator();
    }

    public void Run()
    {
        // Pre-pass: declare a MethodBuilder for every top-level
        // function definition so call sites can resolve them even
        // when the call appears textually before the definition.
        foreach (var statement in _unit.Root.Statements)
        {
            if (statement is BoundFunctionDefinition func)
            {
                DeclareUserFunction(func);
            }
        }

        // Main prologue: wire up the ambient ToshRuntime once so any
        // builtin command dispatched through ToshHost has a runtime
        // available. Idempotent on the host side.
        _il.Emit(OpCodes.Ldnull);
        _il.Emit(OpCodes.Call, s_hostInitialize);

        // Main pass: top-level statements go into Main; function
        // definitions are emitted into their own MethodBuilders and
        // skipped here.
        foreach (var statement in _unit.Root.Statements)
        {
            if (statement is BoundFunctionDefinition func)
            {
                EmitUserFunctionBody(func);
                continue;
            }
            EmitStatement(statement);
        }
        _il.Emit(OpCodes.Ret);
        _program.CreateType();
    }

    private void DeclareUserFunction(BoundFunctionDefinition func)
    {
        if (func.Captures.Count > 0)
        {
            Diagnostics.Add($"function '{func.Name}' captures outer variables (closures unsupported)");
            return;
        }
        if (_userFunctions.ContainsKey(func.Name))
        {
            Diagnostics.Add($"duplicate function definition: '{func.Name}'");
            return;
        }
        foreach (var p in func.Parameters)
        {
            if (p.IsRest || p.IsOptional || p.Default is not null)
            {
                Diagnostics.Add($"function '{func.Name}' uses unsupported parameter shape ('{p.Name}')");
                return;
            }
        }

        var paramTypes = new Type[func.Parameters.Count];
        for (var i = 0; i < paramTypes.Length; i++) paramTypes[i] = typeof(object);
        var method = _program.DefineMethod(
            $"Func_{func.Name}",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(object),
            paramTypes);
        for (var i = 0; i < func.Parameters.Count; i++)
        {
            method.DefineParameter(i + 1, ParameterAttributes.None, func.Parameters[i].Name);
        }
        _userFunctions[func.Name] = new UserFunction(method, func);
    }

    private void EmitUserFunctionBody(BoundFunctionDefinition func)
    {
        if (!_userFunctions.TryGetValue(func.Name, out var entry) || entry.Definition != func)
        {
            // Declaration was rejected (closure / duplicate / bad params).
            return;
        }

        var savedIl = _il;
        var savedLocals = _locals;
        var savedParams = _paramSlots;
        try
        {
            _il = entry.Method.GetILGenerator();
            _locals = new();
            _paramSlots = new();
            for (var i = 0; i < func.Parameters.Count; i++)
            {
                _paramSlots[func.Parameters[i].Symbol] = i;
            }
            foreach (var stmt in func.Body.Statements)
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

    public void SerializeTo(Stream output)
    {
        var metadataBuilder = _ab.GenerateMetadata(out var ilStream, out var mappedFieldData);
        var peHeaderBuilder = new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage);
        var peBuilder = new ManagedPEBuilder(
            header: peHeaderBuilder,
            metadataRootBuilder: new MetadataRootBuilder(metadataBuilder),
            ilStream: ilStream,
            mappedFieldData: mappedFieldData,
            entryPoint: MetadataTokens.MethodDefinitionHandle(_main.MetadataToken));

        var blob = new BlobBuilder();
        peBuilder.Serialize(blob);
        blob.WriteContentTo(output);
    }

    // ─── Statements ───────────────────────────────────────────────

    private void EmitStatement(BoundStatement statement)
    {
        switch (statement)
        {
            case BoundPipelineStatement pipelineStmt:
                EmitPipeline(pipelineStmt.Pipeline, asStatement: true);
                break;

            case BoundVariableDeclaration decl:
                EmitVariableDeclaration(decl);
                break;

            case BoundVariableAssignment assign:
                EmitVariableAssignment(assign);
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

            case BoundFunctionDefinition:
                // Nested function definitions are not yet supported.
                // Top-level ones are handled by Run() before reaching
                // this switch.
                Diagnostics.Add("nested function definitions are not supported");
                break;

            default:
                Diagnostics.Add($"unsupported statement: {statement.GetType().Name}");
                break;
        }
    }

    private void EmitVariableDeclaration(BoundVariableDeclaration decl)
    {
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
        if (assign.Symbol is not null && _paramSlots.ContainsKey(assign.Symbol))
        {
            Diagnostics.Add($"cannot reassign parameter '{assign.Name}'");
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
            _    => null,
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
        EmitBlock(whileStmt.Body);
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
        _il.MarkLabel(topLabelF);

        // Exit when loopVar > end (inclusive upper bound).
        _il.Emit(OpCodes.Ldloc, loopVarLocal);
        _il.Emit(OpCodes.Ldloc, endLocal);
        _il.Emit(OpCodes.Cgt);
        _il.Emit(OpCodes.Brtrue, endLabelF);

        EmitBlock(forStmt.Body);

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

        EmitBlock(forStmt.Body);
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
        if (ret.Value is null)
        {
            _il.Emit(OpCodes.Ldnull);
            _il.Emit(OpCodes.Ret);
            return;
        }
        var t = EmitPipelineAsValue(ret.Value);
        if (t is null)
        {
            _il.Emit(OpCodes.Ldnull);
            _il.Emit(OpCodes.Ret);
            return;
        }
        BoxIfValueType(t);
        _il.Emit(OpCodes.Ret);
    }

    private void EmitBlock(BoundBlock block)
    {
        foreach (var statement in block.Statements)
        {
            EmitStatement(statement);
        }
    }

    // ─── Pipelines ────────────────────────────────────────────────

    private Type? EmitPipeline(BoundPipeline pipeline, bool asStatement)
    {
        if (pipeline.Stages.Count != 1)
        {
            Diagnostics.Add($"unsupported pipeline (stages={pipeline.Stages.Count})");
            return null;
        }

        var stage = pipeline.Stages[0];
        switch (stage)
        {
            case BoundExpressionStage exprStage:
                if (asStatement)
                {
                    var t = EmitExpression(exprStage.Value);
                    if (t is not null) _il.Emit(OpCodes.Pop);
                    return null;
                }
                return EmitExpression(exprStage.Value);

            case BoundCommandCall call when _userFunctions.ContainsKey(call.Name):
                return EmitUserFunctionCall(call, asStatement);

            case BoundCommandCall call when asStatement:
                EmitCommandCallStatement(call);
                return null;

            case BoundCommandCall call:
                return EmitHostInvokeValue(call);

            default:
                Diagnostics.Add($"unsupported pipeline stage: {stage.GetType().Name}");
                return null;
        }
    }

    private Type? EmitPipelineAsValue(BoundPipeline pipeline) => EmitPipeline(pipeline, asStatement: false);

    /// <summary>
    /// Emits a call to a user-defined function. Each argument is
    /// boxed to <see cref="object"/> (the uniform parameter type for
    /// v1). Statement context pops the returned object; value
    /// context returns <see cref="object"/>.
    /// </summary>
    private Type? EmitUserFunctionCall(BoundCommandCall call, bool asStatement)
    {
        var entry = _userFunctions[call.Name];
        var expected = entry.Definition.Parameters.Count;
        if (call.Arguments.Count != expected)
        {
            Diagnostics.Add(
                $"function '{call.Name}' expects {expected} argument(s), got {call.Arguments.Count}");
            return null;
        }
        for (var i = 0; i < call.Arguments.Count; i++)
        {
            var arg = call.Arguments[i];
            if (arg.IsSplat || arg.Name is not null)
            {
                Diagnostics.Add(
                    $"function '{call.Name}': splat/named arguments not yet supported");
                return null;
            }
            var argType = EmitExpression(arg.Value);
            if (argType is null) return null;
            BoxIfValueType(argType);
        }
        _il.Emit(OpCodes.Call, entry.Method);
        if (asStatement)
        {
            _il.Emit(OpCodes.Pop);
            return null;
        }
        return typeof(object);
    }

    private void EmitCommandCallStatement(BoundCommandCall call)
    {
        if (!string.Equals(call.Name, "echo", StringComparison.Ordinal))
        {
            EmitHostInvokeStatement(call);
            return;
        }

        if (call.Arguments.Count == 0)
        {
            _il.Emit(OpCodes.Ldstr, string.Empty);
            _il.Emit(OpCodes.Call, s_writeLineString);
            return;
        }

        if (call.Arguments.Count == 1)
        {
            var argType = EmitExpression(call.Arguments[0].Value);
            if (argType is null) return;
            BoxIfValueType(argType);
            _il.Emit(OpCodes.Call, s_writeLineObject);
            return;
        }

        // Multi-arg: build a string[] and call String.Join(" ", arr).
        _il.Emit(OpCodes.Ldstr, " ");
        _il.Emit(OpCodes.Ldc_I4, call.Arguments.Count);
        _il.Emit(OpCodes.Newarr, typeof(string));

        for (var i = 0; i < call.Arguments.Count; i++)
        {
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldc_I4, i);
            var argType = EmitExpression(call.Arguments[i].Value);
            if (argType is null)
            {
                _il.Emit(OpCodes.Ldstr, "?");
            }
            else
            {
                ConvertToString(argType);
            }
            _il.Emit(OpCodes.Stelem_Ref);
        }

        _il.Emit(OpCodes.Call, s_stringJoin);
        _il.Emit(OpCodes.Call, s_writeLineString);
    }

    /// <summary>
    /// Emits a statement-context dispatch through the runtime host
    /// shim. Pushes <c>name</c> and an <c>object[]</c> of evaluated
    /// arguments, calls <c>ToshHost.InvokeStatement</c>, and pops the
    /// returned "last yielded value". Splat / named args are not yet
    /// supported and emit a diagnostic.
    /// </summary>
    private void EmitHostInvokeStatement(BoundCommandCall call)
    {
        if (!EmitHostArgs(call)) return;
        _il.Emit(OpCodes.Call, s_hostInvokeStatement);
        _il.Emit(OpCodes.Pop);
    }

    /// <summary>
    /// Emits a value-context dispatch through the runtime host shim.
    /// Returns <see cref="object"/> (the unwrapped single value, the
    /// list when multiple were yielded, or null).
    /// </summary>
    private Type? EmitHostInvokeValue(BoundCommandCall call)
    {
        if (!EmitHostArgs(call)) return null;
        _il.Emit(OpCodes.Call, s_hostInvokeValue);
        return typeof(object);
    }

    /// <summary>
    /// Pushes <c>name</c> and an <c>object[]</c> of boxed argument
    /// values onto the eval stack. Returns false (with a diagnostic
    /// recorded) when an argument shape is unsupported, leaving the
    /// stack in an undefined state — callers must abort emission.
    /// </summary>
    private bool EmitHostArgs(BoundCommandCall call)
    {
        _il.Emit(OpCodes.Ldstr, call.Name);
        _il.Emit(OpCodes.Ldc_I4, call.Arguments.Count);
        _il.Emit(OpCodes.Newarr, typeof(object));
        for (var i = 0; i < call.Arguments.Count; i++)
        {
            var arg = call.Arguments[i];
            if (arg.IsSplat || arg.Name is not null)
            {
                Diagnostics.Add(
                    $"command '{call.Name}': splat/named arguments not yet supported");
                return false;
            }
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldc_I4, i);
            var argType = EmitExpression(arg.Value);
            if (argType is null) return false;
            BoxIfValueType(argType);
            _il.Emit(OpCodes.Stelem_Ref);
        }
        return true;
    }

    // ─── Expressions ──────────────────────────────────────────────

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

            case BoundIndexAccess index:
                return EmitIndexAccess(index);

            case BoundArrayLiteral arr:
                return EmitArrayLiteral(arr);

            case BoundRecordLiteral rec:
                return EmitRecordLiteral(rec);

            case BoundDictLiteral dict:
                return EmitDictLiteral(dict);

            default:
                Diagnostics.Add($"unsupported expression: {expression.GetType().Name}");
                return null;
        }
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
        if (varRef.Symbol is not null && _paramSlots.TryGetValue(varRef.Symbol, out var paramIndex))
        {
            _il.Emit(OpCodes.Ldarg, paramIndex);
            return typeof(object);
        }
        if (varRef.Symbol is null || !_locals.TryGetValue(varRef.Symbol, out var slot))
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
    private Type? EmitMemberAccess(BoundMemberAccess member)
    {
        var t = EmitExpression(member.Target);
        if (t is null) return null;
        BoxIfValueType(t);
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
    /// Emits a list literal as <c>new List&lt;object?&gt;()</c>
    /// followed by <c>Add</c> calls for each item. Spread elements
    /// (<c>...$xs</c>) aren't yet supported and emit a diagnostic.
    /// </summary>
    private Type? EmitArrayLiteral(BoundArrayLiteral arr)
    {
        _il.Emit(OpCodes.Newobj, s_listCtor);
        foreach (var item in arr.Items)
        {
            if (item.IsSpread)
            {
                Diagnostics.Add("array literal: spread elements not yet supported");
                return null;
            }
            _il.Emit(OpCodes.Dup);
            var t = EmitExpression(item.Value);
            if (t is null) return null;
            BoxIfValueType(t);
            _il.Emit(OpCodes.Callvirt, s_listAdd);
        }
        return s_listOfObject;
    }

    /// <summary>
    /// Emits a record literal (<c>{ name: "x", age: 1 }</c>) as
    /// <c>new Dictionary&lt;string, object?&gt;()</c> with one
    /// indexer-set per field. Computed-name and spread entries
    /// aren't yet supported.
    /// </summary>
    private Type? EmitRecordLiteral(BoundRecordLiteral rec)
    {
        _il.Emit(OpCodes.Newobj, s_dictCtor);
        foreach (var entry in rec.Fields)
        {
            if (entry is not BoundRecordField field)
            {
                Diagnostics.Add(
                    $"record literal: '{entry.GetType().Name}' entries not yet supported");
                return null;
            }
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldstr, field.Name);
            var vt = EmitExpression(field.Value);
            if (vt is null) return null;
            BoxIfValueType(vt);
            _il.Emit(OpCodes.Callvirt, s_dictSetItem);
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

    private Type? EmitBinaryOperator(BoundBinaryOperator binOp)
    {
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
                Diagnostics.Add($"unsupported binary operator: '{binOp.Operator}'");
                return null;
        }
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
                Diagnostics.Add($"unsupported unary operator: '{unOp.Operator}'");
                return null;
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

    private readonly record struct LocalSlot(LocalBuilder Local, Type Type);

    private readonly record struct UserFunction(MethodBuilder Method, BoundFunctionDefinition Definition);
}
