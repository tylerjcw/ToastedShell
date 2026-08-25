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
    /// <param name="asSequence">
    /// When true the pipeline's value is the full item sequence (an
    /// iteration source). Multi-stage value pipelines otherwise use the
    /// interpreter's zero/one/many subexpression collapse (TS-P1-20).
    /// </param>
    /// <param name="requireSingleSubexpressionValue">
    /// Selects that same collapse for a single-stage built-in command. Set
    /// only by <see cref="EmitPipelineAsSubexpressionValue"/>; other value
    /// consumers retain their established collection materialization.
    /// </param>
    private Type? EmitPipeline(
        BoundPipeline pipeline,
        bool asStatement,
        bool asSequence = false,
        bool requireSingleSubexpressionValue = false)
    {
        if (pipeline.Stages.Count == 0)
        {
            Diagnostics.Add("empty pipeline");
            return null;
        }

        var hasRedirections = pipeline.BoundRedirections.Count > 0
            || pipeline.BoundInputRedirection is not null;

        if (((PipelineSyntax)pipeline.Original).IsBackground)
        {
            Diagnostics.Add("background pipelines (`&`) are not yet supported in compiled tosh");
            return null;
        }

        if (!hasRedirections)
        {
            return EmitPipelineCore(
                pipeline,
                asStatement,
                asSequence,
                requireSingleSubexpressionValue);
        }

        // Redirection wrapping. Evaluate target expressions, build
        // streams/modes/targets arrays, call ToshHost.BeginRedirection,
        // then run the body inside try/finally.
        return EmitPipelineWithRedirections(
            pipeline,
            asStatement,
            asSequence,
            requireSingleSubexpressionValue);
    }

    private Type? EmitPipelineCore(
        BoundPipeline pipeline,
        bool asStatement,
        bool asSequence = false,
        bool requireSingleSubexpressionValue = false)
    {
        if (pipeline.Stages.Count >= 2)
        {
            return EmitMultiStagePipeline(pipeline, asStatement, asSequence);
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
                return EmitHostInvokeValue(call, requireSingleSubexpressionValue);

            default:
                Diagnostics.Add($"unsupported pipeline stage: {stage.GetType().Name}");
                return null;
        }
    }

    private Type? EmitPipelineWithRedirections(
        BoundPipeline pipeline,
        bool asStatement,
        bool asSequence = false,
        bool requireSingleSubexpressionValue = false)
    {
        // Stream redirection requires opening files, swapping
        // Console.Out/Error/In, and tracking a disposable scope —
        // all of which route through ToshHost.BeginRedirection. That
        // is by definition a Tier 2 (runtime) feature, so the Pure
        // profile must reject it loudly rather than silently
        // accepting an emit that would call into the host at run
        // time.
        RequireTier(2, "stream redirection (out>/err>/in</etc.)");
        var redirs = pipeline.BoundRedirections;
        var n = redirs.Count;

        // int[] streams
        var streamsLocal = _il.DeclareLocal(typeof(int[]));
        _il.Emit(OpCodes.Ldc_I4, n);
        _il.Emit(OpCodes.Newarr, typeof(int));
        _il.Emit(OpCodes.Stloc, streamsLocal);
        for (var i = 0; i < n; i++)
        {
            _il.Emit(OpCodes.Ldloc, streamsLocal);
            _il.Emit(OpCodes.Ldc_I4, i);
            _il.Emit(OpCodes.Ldc_I4, (int)redirs[i].Stream);
            _il.Emit(OpCodes.Stelem_I4);
        }

        // int[] modes
        var modesLocal = _il.DeclareLocal(typeof(int[]));
        _il.Emit(OpCodes.Ldc_I4, n);
        _il.Emit(OpCodes.Newarr, typeof(int));
        _il.Emit(OpCodes.Stloc, modesLocal);
        for (var i = 0; i < n; i++)
        {
            _il.Emit(OpCodes.Ldloc, modesLocal);
            _il.Emit(OpCodes.Ldc_I4, i);
            _il.Emit(OpCodes.Ldc_I4, (int)redirs[i].Mode);
            _il.Emit(OpCodes.Stelem_I4);
        }

        // string[] targets — evaluate each target expression into a string
        var targetsLocal = _il.DeclareLocal(typeof(string[]));
        _il.Emit(OpCodes.Ldc_I4, n);
        _il.Emit(OpCodes.Newarr, typeof(string));
        _il.Emit(OpCodes.Stloc, targetsLocal);
        for (var i = 0; i < n; i++)
        {
            _il.Emit(OpCodes.Ldloc, targetsLocal);
            _il.Emit(OpCodes.Ldc_I4, i);
            var t = EmitExpression(redirs[i].Target);
            if (t is null)
            {
                Diagnostics.Add("redirection: target expression failed to emit");
                return null;
            }
            BoxIfValueType(t);
            _il.Emit(OpCodes.Call, s_hostAsRedirectionPath);
            _il.Emit(OpCodes.Stelem_Ref);
        }

        // string? inputPath -> stash in a local
        var inputPathLocal = _il.DeclareLocal(typeof(string));
        if (pipeline.BoundInputRedirection is { } inputRedir)
        {
            var t = EmitExpression(inputRedir.Source);
            if (t is null)
            {
                Diagnostics.Add("input redirection: source expression failed to emit");
                return null;
            }
            BoxIfValueType(t);
            _il.Emit(OpCodes.Call, s_hostAsRedirectionPath);
            _il.Emit(OpCodes.Stloc, inputPathLocal);
        }
        else
        {
            _il.Emit(OpCodes.Ldnull);
            _il.Emit(OpCodes.Stloc, inputPathLocal);
        }

        // RedirectionScope scope = ToshHost.BeginRedirection(streams, modes, targets, inputPath);
        var scopeLocal = _il.DeclareLocal(typeof(global::Tosh.Compiler.Runtime.ToshHost.RedirectionScope));
        _il.Emit(OpCodes.Ldloc, streamsLocal);
        _il.Emit(OpCodes.Ldloc, modesLocal);
        _il.Emit(OpCodes.Ldloc, targetsLocal);
        _il.Emit(OpCodes.Ldloc, inputPathLocal);
        _il.Emit(OpCodes.Call, s_hostBeginRedirection);
        _il.Emit(OpCodes.Stloc, scopeLocal);

        // Reserve a result local in case asStatement=false.
        LocalBuilder? resultLocal = null;
        Type? resultType = null;

        _il.BeginExceptionBlock();

        var bodyType = EmitPipelineCore(
            pipeline,
            asStatement,
            asSequence,
            requireSingleSubexpressionValue);
        if (!asStatement && bodyType is not null)
        {
            BoxIfValueType(bodyType);
            resultLocal = _il.DeclareLocal(typeof(object));
            _il.Emit(OpCodes.Stloc, resultLocal);
            resultType = typeof(object);
        }

        _il.BeginFinallyBlock();
        _il.Emit(OpCodes.Ldloc, scopeLocal);
        var brScopeNull = _il.DefineLabel();
        _il.Emit(OpCodes.Brfalse_S, brScopeNull);
        _il.Emit(OpCodes.Ldloc, scopeLocal);
        _il.Emit(OpCodes.Callvirt,
            typeof(IDisposable).GetMethod(nameof(IDisposable.Dispose))!);
        _il.MarkLabel(brScopeNull);
        _il.EndExceptionBlock();

        if (!asStatement && resultLocal is not null)
        {
            _il.Emit(OpCodes.Ldloc, resultLocal);
            return resultType;
        }
        return asStatement ? null : bodyType;
    }

    private Type? EmitPipelineAsValue(BoundPipeline pipeline) => EmitPipeline(pipeline, asStatement: false);

    /// <summary>
    /// Emits a parenthesized pipeline in expression position. A single-stage
    /// built-in command normally reaches <c>InvokeValue</c>, which packages
    /// multiple yielded values into a collection; a subexpression instead uses
    /// the same zero/one/many collapse as the interpreter and multi-stage emitter.
    /// </summary>
    private Type? EmitPipelineAsSubexpressionValue(BoundPipeline pipeline) =>
        EmitPipeline(
            pipeline,
            asStatement: false,
            requireSingleSubexpressionValue: true);

    /// <summary>
    /// Emits a pipeline whose value is consumed as a sequence (a
    /// <c>for … in</c> source), so every produced item is preserved
    /// instead of being collapsed to a single value.
    /// </summary>
    private Type? EmitPipelineAsSequence(BoundPipeline pipeline) => EmitPipeline(pipeline, asStatement: false, asSequence: true);

    /// <summary>
    /// Emits IL for a 2+ stage pipeline. Each stage is dispatched
    /// through <see cref="global::Tosh.Compiler.Runtime.ToshHost.RunStage"/>
    /// which receives the previous stage's
    /// <see cref="IAsyncEnumerable{T}"/> as input. The accumulator
    /// is held in a single local of type <c>IAsyncEnumerable&lt;object?&gt;</c>
    /// so each stage's IL is short and uniform. The terminal call
    /// is either <c>DrainStatement</c> (statement context) or
    /// <c>DrainValue</c> (value context, returns
    /// <see cref="List{T}"/>).
    ///
    /// v1 limitations:
    ///   • User-defined functions cannot be pipeline stages.
    ///   • Splat / named arguments still surface as diagnostics.
    /// </summary>
    private Type? EmitMultiStagePipeline(BoundPipeline pipeline, bool asStatement, bool asSequence = false)
    {
        if (TryEmitDirectIgnorePipeline(pipeline, asStatement, asSequence, out var ignoreResultType))
        {
            return ignoreResultType;
        }

        if (!asStatement
            && !asSequence
            && TryEmitDirectCountValuePipeline(pipeline))
        {
            return typeof(int);
        }

        // Reusable accumulator local: each stage replaces it.
        var accLocal = _il.DeclareLocal(typeof(IAsyncEnumerable<object?>));

        // Stage 0: either a command call (Phase 1) or an arbitrary
        // expression seeding the pipeline (Phase 3).
        switch (pipeline.Stages[0])
        {
            case BoundCommandCall first when TryResolveUserFunctionEntry(first, out var firstUserEntry):
                if (!EmitUserFuncPipelineStage(first, firstUserEntry, isFirstStage: true, accLocal))
                    return null;
                break;

            case BoundCommandCall first:
                // ResolveCommand(name) → RunStage(cmd, EmptyInput(), args0)
                _il.Emit(OpCodes.Ldstr, first.Name);
                _il.Emit(OpCodes.Call, s_hostResolveCommand);
                _il.Emit(OpCodes.Call, s_hostEmptyInput);
                if (!EmitStageArgsArray(first)) return null;
                RequireTier(2, "builtin command dispatch (pipeline stage)");
                _il.Emit(OpCodes.Call, s_hostRunStage);
                break;

            // `TOAST-0040`. `...value` as the head. Must precede the general expression
            // stage below, because a spread is not a value to seed from — the author has
            // already said the elements are what they mean.
            case BoundExpressionStage { Value: BoundSpreadElement spreadHead }:
                var spreadType = EmitExpression(spreadHead.Value);
                if (spreadType is null) return null;
                BoxIfValueType(spreadType);
                _il.Emit(OpCodes.Call, s_hostSeedFromSpread);
                break;

            case BoundExpressionStage exprStage:
                // SeedFromValue(<expr>) — boxes the value (if needed)
                // and turns it into IAsyncEnumerable<object?>.
                var exprType = EmitExpression(exprStage.Value);
                if (exprType is null) return null;
                BoxIfValueType(exprType);
                _il.Emit(OpCodes.Call, s_hostSeedFromValue);
                break;

            default:
                Diagnostics.Add(
                    $"unsupported first pipeline stage: {pipeline.Stages[0].GetType().Name}");
                return null;
        }
        _il.Emit(OpCodes.Stloc, accLocal);

        // Stages 1..N-1: chain through RunStage(cmd, acc, args).
        for (var i = 1; i < pipeline.Stages.Count; i++)
        {
            if (pipeline.Stages[i] is not BoundCommandCall stage)
            {
                Diagnostics.Add(
                    $"non-command pipeline stage at position {i}: {pipeline.Stages[i].GetType().Name}");
                return null;
            }
            if (TryResolveUserFunctionEntry(stage, out var userEntry))
            {
                if (!EmitUserFuncPipelineStage(stage, userEntry, isFirstStage: false, accLocal))
                    return null;
                _il.Emit(OpCodes.Stloc, accLocal);
                continue;
            }

            _il.Emit(OpCodes.Ldstr, stage.Name);
            _il.Emit(OpCodes.Call, s_hostResolveCommand);
            _il.Emit(OpCodes.Ldloc, accLocal);
            if (!EmitStageArgsArray(stage)) return null;
            RequireTier(2, "builtin command dispatch (multi-stage pipeline)");
            _il.Emit(OpCodes.Call, s_hostRunStage);
            _il.Emit(OpCodes.Stloc, accLocal);
        }

        // Drain.
        _il.Emit(OpCodes.Ldloc, accLocal);
        if (asStatement)
        {
            _il.Emit(OpCodes.Call, s_hostDrainStatement);
            return null;
        }
        if (asSequence)
        {
            _il.Emit(OpCodes.Call, s_hostDrainValue);
            return s_listOfObject;
        }
        _il.Emit(OpCodes.Call, s_hostDrainSubexpressionValue);
        return typeof(object);
    }

    /// <summary>
    /// Emits <c>expression | ignore</c> directly. The expression is still fully
    /// enumerated so lazy and asynchronous sources retain their side effects; only
    /// the command-host lookup and empty output stream are elided.
    /// </summary>
    private bool TryEmitDirectIgnorePipeline(
        BoundPipeline pipeline,
        bool asStatement,
        bool asSequence,
        out Type? resultType)
    {
        resultType = null;
        if (pipeline.Stages.Count != 2
            || pipeline.Stages[0] is not BoundExpressionStage expression
            || pipeline.Stages[1] is not BoundCommandCall
            {
                Name: "ignore",
                Arguments.Count: 0,
            })
        {
            return false;
        }

        var valueType = EmitExpression(expression.Value);
        if (valueType is null)
        {
            return true;
        }

        BoxIfValueType(valueType);
        _il.Emit(OpCodes.Call, s_ignoreExpressionPipelineItems);

        if (asStatement)
        {
            return true;
        }

        if (asSequence)
        {
            _il.Emit(OpCodes.Newobj, s_listCtor);
            resultType = s_listOfObject;
            return true;
        }

        _il.Emit(OpCodes.Ldnull);
        resultType = typeof(object);
        return true;
    }

    /// <summary>
    /// Emits <c>(expression | count)</c> directly when <c>count</c> has no arguments.
    /// The portable runtime helper owns collection shape, including scalar strings,
    /// records, dictionaries, null, and asynchronous expression values. Statement and
    /// sequence contexts keep the ordinary command-host path because they have different
    /// output/cardinality contracts.
    /// </summary>
    private bool TryEmitDirectCountValuePipeline(BoundPipeline pipeline)
    {
        if (pipeline.Stages.Count != 2
            || pipeline.Stages[0] is not BoundExpressionStage expression
            || pipeline.Stages[1] is not BoundCommandCall
            {
                Name: "count",
                Arguments.Count: 0,
            })
        {
            return false;
        }

        var valueType = EmitExpression(expression.Value);
        if (valueType is null)
        {
            return true;
        }

        BoxIfValueType(valueType);
        _il.Emit(OpCodes.Call, s_countExpressionPipelineItems);
        return true;
    }

    /// <summary>
    /// Pushes an <c>object[]</c> of evaluated, boxed arguments for
    /// a single pipeline stage. Named arguments are wrapped in a
    /// <see cref="global::Tosh.Language.NamedArgument"/> instance
    /// (matching what the interpreter passes commands). Splat
    /// arguments expand at runtime via
    /// <see cref="global::Tosh.Compiler.Runtime.ToshHost.SpreadArgs"/>;
    /// when any splat is present we build the array via
    /// <c>List&lt;object?&gt;.ToArray()</c> instead of a
    /// fixed-length allocation.
    /// </summary>
    private bool EmitStageArgsArray(BoundCommandCall call)
        => EmitArgsArray(call);

    /// <summary>
    /// Emits a user function as a pipeline stage. Pushes onto the
    /// stack the <see cref="IAsyncEnumerable{T}"/> produced by
    /// <see cref="global::Tosh.Compiler.Runtime.ToshHost.RunUserFuncStage"/>
    /// — the caller is responsible for storing it back into the
    /// pipeline accumulator. Validates arity at compile time:
    /// the user function must take either exactly the call's
    /// argument count (ignores input) or exactly one more
    /// (takes one input element per call as the leading
    /// parameter).
    /// </summary>
    private bool EmitUserFuncPipelineStage(
        BoundCommandCall stage,
        UserFunction entry,
        bool isFirstStage,
        LocalBuilder accLocal)
    {
        var paramCount = entry.Definition.Parameters.Count;
        var argCount = stage.Arguments.Count;
        var hasSplat = false;
        foreach (var a in stage.Arguments)
        {
            if (a.IsSplat) { hasSplat = true; break; }
        }
        // With splat the effective arg count isn't known until runtime;
        // RunUserFuncStage performs the arity check there.
        if (!hasSplat && paramCount != argCount && paramCount != argCount + 1)
        {
            Diagnostics.Add(
                $"user function '{stage.Name}' as a pipeline stage expects "
                + $"{argCount} or {argCount + 1} parameters, got {paramCount}");
            return false;
        }

        // ldtoken methodBuilder + Call MethodBase.GetMethodFromHandle
        // → MethodInfo. PersistedAssemblyBuilder resolves the token
        // lazily after the assembly is loaded. Pipeline-stage
        // dispatch targets the user function's canonical method
        // (typed primary for typed funcs, dynamic Func_<name>
        // for untyped). Per-parameter arg coercion happens in
        // ToshHost.InvokeUserFunc.
        _il.Emit(OpCodes.Ldtoken, entry.Method);
        _il.Emit(OpCodes.Call, s_methodBaseGetFromHandle);
        _il.Emit(OpCodes.Castclass, typeof(MethodInfo));
        _il.Emit(OpCodes.Ldc_I4, paramCount);
        if (isFirstStage)
        {
            _il.Emit(OpCodes.Call, s_hostEmptyInput);
        }
        else
        {
            _il.Emit(OpCodes.Ldloc, accLocal);
        }
        if (!EmitArgsArray(stage)) return false;
        RequireTier(2, "user function dispatch via host (pipeline stage)");
        _il.Emit(OpCodes.Call, s_hostRunUserFuncStage);
        return true;
    }

    /// <summary>
    /// Pushes a freshly-built <see cref="ShellBlock"/> onto the eval
    /// stack: <c>ToshHost.MakeBlock(span.Start, span.Length, captures)</c>.
    /// Captures are materialized as a <c>Dictionary&lt;string,object?&gt;</c>
    /// snapshot of the named locals/params the binder identified.
    /// </summary>
    private void EmitMakeBlock(BoundBlockExpression block)
    {
        if (CanCompileBlockBody(block))
        {
            // Build the subset of captures that must be passed at runtime
            // (static-field captures remain accessible directly in the body).
            var runtimeCaptures = new List<BoundSymbol>(block.Captures.Count);
            foreach (var c in block.Captures)
            {
                if (!_staticFields.ContainsKey(c)) runtimeCaptures.Add(c);
            }
            var captureIndices = new Dictionary<BoundSymbol, int>(runtimeCaptures.Count);
            for (var i = 0; i < runtimeCaptures.Count; i++)
                captureIndices[runtimeCaptures[i]] = i;

            var blockMethod = EmitBlockBodyMethod(block, runtimeCaptures, captureIndices);
            if (blockMethod is not null)
            {
                // new Func<object?,object[],List<object?>>(null, ldftn __block_N)
                _il.Emit(OpCodes.Ldnull);
                _il.Emit(OpCodes.Ldftn, blockMethod);
                _il.Emit(OpCodes.Newobj, s_funcBlockBodyCtor);

                // captureValues = new object[runtimeCaptures.Count] { ... }
                _il.Emit(OpCodes.Ldc_I4, runtimeCaptures.Count);
                _il.Emit(OpCodes.Newarr, typeof(object));
                for (var i = 0; i < runtimeCaptures.Count; i++)
                {
                    _il.Emit(OpCodes.Dup);
                    _il.Emit(OpCodes.Ldc_I4, i);
                    var cap = runtimeCaptures[i];
                    if (_typedParamLocals.TryGetValue(cap, out var typedLocal))
                        _il.Emit(OpCodes.Ldloc, typedLocal);
                    else if (_paramSlots.TryGetValue(cap, out var pIdx))
                        _il.Emit(OpCodes.Ldarg, pIdx);
                    else if (_staticFields.TryGetValue(cap, out var sf))
                        _il.Emit(OpCodes.Ldsfld, sf);
                    else if (_locals.TryGetValue(cap, out var s))
                    {
                        _il.Emit(OpCodes.Ldloc, s.Local);
                        BoxIfValueType(s.Type);
                    }
                    else
                    {
                        Diagnostics.Add($"block capture '{cap.Name}' has no IL slot");
                        _il.Emit(OpCodes.Ldnull);
                    }
                    _il.Emit(OpCodes.Stelem_Ref);
                }
                _il.Emit(OpCodes.Call, s_hostMakeCompiledBlock);
                return;
            }
        }

        // Fallback: source-replay.
        EmitMakeBlockFallback(block);
    }

    private static bool CanCompileBlockBody(BoundBlockExpression block)
    {
        if (block.Body.Statements.Count == 0) return true;
        if (block.Body.Statements.Count != 1) return false;
        if (block.Body.Statements[0] is not BoundPipelineStatement ps) return false;
        if (ps.Pipeline.Stages.Count != 1) return false;
        return ps.Pipeline.Stages[0] is BoundExpressionStage or BoundCommandCall;
    }

    private MethodBuilder? EmitBlockBodyMethod(
        BoundBlockExpression block,
        List<BoundSymbol> runtimeCaptures,
        Dictionary<BoundSymbol, int> captureIndices)
    {
        var methodName = $"__block_{block.Body.Span.Start}";
        var blockMethod = _program.DefineMethod(
            methodName,
            // `TOAST-0035`. Assembly-visible rather than private: a block argument written
            // inside a *module* method emits its helper here, on `Program`, and a module
            // shell is a different type. Private made that a `MethodAccessException` at the
            // first call — which only became reachable when such modules stopped being
            // replayed. Everything this can be called from is in the emitted assembly.
            MethodAttributes.Assembly | MethodAttributes.Static,
            typeof(List<object>),
            new[] { typeof(object), typeof(object[]) });

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
        var savedReturnEmissionFrame = _returnEmissionFrame;
        var savedDeferredCleanupFrames = _deferredCleanupFrames;
        try
        {
            _il = blockMethod.GetILGenerator();
            _locals = new();
            _paramSlots = new();
            _typedParamLocals = new();
            _currentFunctionReturnType = null;
            _currentFunctionReturnRefinement = null;
            _currentThisType = null;
            _underscoreStack = new();
            _loopStack = new();
            _blockCaptureIndices = captureIndices;
            _returnEmissionFrame = null;
            _deferredCleanupFrames = new();

            var resultsLocal = _il.DeclareLocal(typeof(List<object>));
            _blockOutputLocal = resultsLocal;
            var executionFrame = EmitExecutionFrameEntry("block");
            _il.Emit(OpCodes.Newobj, s_listCtor);
            _il.Emit(OpCodes.Stloc, resultsLocal);

            if (block.Body.Statements.Count == 1
                && block.Body.Statements[0] is BoundPipelineStatement ps)
            {
                if (!EmitBlockBodyPipelineStatement(ps))
                    return null;
            }

            EmitExecutionFrameExit(executionFrame);
            _il.Emit(OpCodes.Ldloc, resultsLocal);
            _il.Emit(OpCodes.Ret);
            return blockMethod;
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
            _returnEmissionFrame = savedReturnEmissionFrame;
            _deferredCleanupFrames = savedDeferredCleanupFrames;
        }
    }

    private bool EmitBlockBodyPipelineStatement(BoundPipelineStatement ps)
    {
        var stage = ps.Pipeline.Stages[0];
        switch (stage)
        {
            case BoundExpressionStage exprStage:
                {
                    var t = EmitExpression(exprStage.Value);
                    if (t is null) return false;
                    BoxIfValueType(t);
                    var tmp = _il.DeclareLocal(typeof(object));
                    _il.Emit(OpCodes.Stloc, tmp);
                    var skip = _il.DefineLabel();
                    _il.Emit(OpCodes.Ldloc, tmp);
                    _il.Emit(OpCodes.Brfalse_S, skip);
                    _il.Emit(OpCodes.Ldloc, _blockOutputLocal!);
                    _il.Emit(OpCodes.Ldloc, tmp);
                    _il.Emit(OpCodes.Callvirt, s_listAdd);
                    _il.MarkLabel(skip);
                    return true;
                }

            case BoundCommandCall call when _userFunctions.ContainsKey(call.Name):
                {
                    var t = EmitUserFunctionCall(call, asStatement: false);
                    if (t is null) return false;
                    BoxIfValueType(t);
                    var tmp = _il.DeclareLocal(typeof(object));
                    _il.Emit(OpCodes.Stloc, tmp);
                    var skip = _il.DefineLabel();
                    _il.Emit(OpCodes.Ldloc, tmp);
                    _il.Emit(OpCodes.Brfalse_S, skip);
                    _il.Emit(OpCodes.Ldloc, _blockOutputLocal!);
                    _il.Emit(OpCodes.Ldloc, tmp);
                    _il.Emit(OpCodes.Callvirt, s_listAdd);
                    _il.MarkLabel(skip);
                    return true;
                }

            case BoundCommandCall call:
                {
                    if (!EmitHostArgs(call)) return false;
                    RequireTier(2, "command invocation (block collect)");
                    _il.Emit(OpCodes.Call, s_hostInvokeCollect);
                    var items = _il.DeclareLocal(typeof(object[]));
                    _il.Emit(OpCodes.Stloc, items);
                    _il.Emit(OpCodes.Ldloc, _blockOutputLocal!);
                    _il.Emit(OpCodes.Ldloc, items);
                    _il.Emit(OpCodes.Callvirt, s_listAddRange);
                    return true;
                }

            default:
                Diagnostics.Add($"unsupported block body stage: {stage.GetType().Name}");
                return false;
        }
    }

    private void EmitMakeBlockFallback(BoundBlockExpression block)
    {
        RequireTier(3, "block argument (re-evaluates source at runtime)");
        _il.Emit(OpCodes.Ldc_I4, block.Body.Span.Start);
        _il.Emit(OpCodes.Ldc_I4, block.Body.Span.Length);
        if (block.Captures.Count == 0)
        {
            _il.Emit(OpCodes.Ldnull);
        }
        else
        {
            _il.Emit(OpCodes.Newobj, s_dictCtor);
            foreach (var capture in block.Captures)
            {
                _il.Emit(OpCodes.Dup);
                _il.Emit(OpCodes.Ldstr, capture.Name);
                if (_typedParamLocals.TryGetValue(capture, out var typedParamLocal))
                    _il.Emit(OpCodes.Ldloc, typedParamLocal);
                else if (_paramSlots.TryGetValue(capture, out var paramIndex))
                    _il.Emit(OpCodes.Ldarg, paramIndex);
                else if (_locals.TryGetValue(capture, out var slot))
                {
                    _il.Emit(OpCodes.Ldloc, slot.Local);
                    BoxIfValueType(slot.Type);
                }
                else
                {
                    Diagnostics.Add($"block capture '{capture.Name}' has no IL slot");
                    _il.Emit(OpCodes.Ldnull);
                }
                _il.Emit(OpCodes.Callvirt, s_dictSetItem);
            }
        }
        _il.Emit(OpCodes.Call, s_hostMakeBlock);
    }

    private bool TryResolveUserFunctionEntry(BoundCommandCall call, out UserFunction entry)
    {
        entry = default!;
        if (!_userFunctions.TryGetValue(call.Name, out var overloads) || overloads.Count == 0)
        {
            return false;
        }

        if (overloads.Count == 1)
        {
            entry = overloads[0];
            return true;
        }

        if (call.OverloadIndex is int idx && idx >= 0 && idx < overloads.Count)
        {
            entry = overloads[idx];
            return true;
        }

        // Binder deliberately leaves OverloadIndex null for ties / no-match.
        // Let runtime command dispatch resolve those cases.
        return false;
    }

    private Type? EmitUserFunctionOverloadDispatch(
        BoundCommandCall call,
        List<UserFunction> overloads,
        bool asStatement)
    {
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

        if (!EmitArgsArray(call)) return null;
        _il.Emit(OpCodes.Call, s_hostInvokeUserOverload);
        if (asStatement)
        {
            _il.Emit(OpCodes.Pop);
            return null;
        }

        return typeof(object);
    }

    /// <summary>
    /// Emits a call to a user-defined function. Untyped callees take
    /// <c>object</c> for every parameter and return <c>object</c>;
    /// args are evaluated and boxed in declaration order. Fully
    /// typed callees use their declared CLR signature directly —
    /// args are coerced to the typed param shape through the canonical
    /// annotation conversion boundary, and the return is
    /// produced in its declared CLR type. Statement context pops
    /// the unused return value.
    /// </summary>
    private Type? EmitUserFunctionCall(BoundCommandCall call, bool asStatement)
    {
        if (!_userFunctions.TryGetValue(call.Name, out var overloads) || overloads.Count == 0)
        {
            if (asStatement)
            {
                EmitHostInvokeStatement(call);
                return null;
            }

            return EmitHostInvokeValue(call);
        }

        if (!TryResolveUserFunctionEntry(call, out var entry))
        {
            return EmitUserFunctionOverloadDispatch(call, overloads, asStatement);
        }

        if (entry.UsesPackedArguments)
        {
            if (!EmitArgsArray(call)) return null;
            _il.Emit(OpCodes.Call, entry.Method);
            if (asStatement)
            {
                _il.Emit(OpCodes.Pop);
                return null;
            }

            // `TOAST-0066`. Only a pipeline stage distinguishes "produced nothing" from
            // "returned null"; in value position the reader sees null, so the sentinel is
            // normalised away here rather than escaping into a value they could hold.
            _il.Emit(OpCodes.Call, s_noValueNormalize);
            return typeof(object);
        }

        var expected = entry.Definition.Parameters.Count;
        if (call.Arguments.Count != expected)
        {
            return EmitUserFunctionOverloadDispatch(call, overloads, asStatement);
        }

        for (var i = 0; i < call.Arguments.Count; i++)
        {
            var arg = call.Arguments[i];
            if (arg.IsSplat || arg.Name is not null)
            {
                return EmitUserFunctionOverloadDispatch(call, overloads, asStatement);
            }

            var argType = EmitExpression(arg.Value);
            if (argType is null) return null;
            if (entry.IsTyped)
            {
                var target = entry.ParamClrTypes[i];
                if (target == typeof(object))
                {
                    BoxIfValueType(argType);
                }
                else if (target != argType)
                {
                    if (argType.IsValueType) _il.Emit(OpCodes.Box, argType);
                    CoerceObjectToTyped(
                        _il,
                        target,
                        entry.Definition.Parameters[i].TypeName,
                        arg.Value.Span,
                        $"parameter '{entry.Definition.Parameters[i].Name}'");
                }
            }
            else
            {
                BoxIfValueType(argType);
            }
        }

        _il.Emit(OpCodes.Call, entry.Method);
        var resultType = entry.IsTyped ? entry.ReturnClrType : typeof(object);
        if (asStatement)
        {
            _il.Emit(OpCodes.Pop);
            return null;
        }

        if (!entry.IsTyped)
        {
            // `TOAST-0066`, as above — a typed function never produces the sentinel.
            _il.Emit(OpCodes.Call, s_noValueNormalize);
        }

        return resultType;
    }

}
