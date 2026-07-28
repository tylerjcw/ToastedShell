using System.Reflection;
using System.Reflection.Emit;
using Tosh.Compiler.IR;
using Tosh.Runtime;

namespace Tosh.Compiler;

internal sealed partial class EmitterImpl
{
    private static readonly Type s_deferFailureStateType =
        typeof(global::Tosh.Runtime.ToshDeferFailureState);
    private static readonly ConstructorInfo s_deferFailureStateCtor =
        s_deferFailureStateType.GetConstructor(Type.EmptyTypes)!;
    private static readonly MethodInfo s_deferCaptureBodyFailure =
        s_deferFailureStateType.GetMethod(
            nameof(global::Tosh.Runtime.ToshDeferFailureState.CaptureBodyFailure),
            new[] { typeof(Exception) })!;
    private static readonly MethodInfo s_deferCaptureCleanupFailure =
        s_deferFailureStateType.GetMethod(
            nameof(global::Tosh.Runtime.ToshDeferFailureState.CaptureCleanupFailure),
            new[] { typeof(Exception) })!;
    private static readonly MethodInfo s_deferThrowIfCleanupFailed =
        s_deferFailureStateType.GetMethod(
            nameof(global::Tosh.Runtime.ToshDeferFailureState.ThrowIfCleanupFailed),
            Type.EmptyTypes)!;

    /// <summary>
    /// Active method return target. Every source-emitting method gets one
    /// epilogue outside all protected regions so <c>return</c> can use
    /// <c>leave</c>, allowing intervening defer/finally handlers to execute.
    /// </summary>
    private ReturnEmissionFrame? _returnEmissionFrame;

    /// <summary>
    /// Deferred cleanup bodies suppress owner-scope return/break/continue.
    /// The loop depth recorded on entry distinguishes an owner loop from a
    /// loop declared wholly inside the cleanup, whose control flow remains
    /// ordinary.
    /// </summary>
    private Stack<DeferredCleanupEmissionFrame> _deferredCleanupFrames = new();

    private readonly record struct ReturnEmissionFrame(
        Type ReturnType,
        LocalBuilder? ValueLocal,
        Label Epilogue);

    private readonly record struct DeferredCleanupEmissionFrame(
        Label ExitLabel,
        int OwningLoopDepth);

    private ReturnEmissionFrame CreateReturnEmissionFrame(
        Type returnType,
        LocalBuilder? existingValueLocal = null)
    {
        var valueLocal = returnType == typeof(void)
            ? null
            : existingValueLocal ?? _il.DeclareLocal(returnType);
        return new ReturnEmissionFrame(
            returnType,
            valueLocal,
            _il.DefineLabel());
    }

    private void EmitReturnEpilogue(ReturnEmissionFrame frame)
    {
        _il.MarkLabel(frame.Epilogue);
        if (frame.ValueLocal is not null)
        {
            _il.Emit(OpCodes.Ldloc, frame.ValueLocal);
        }
        _il.Emit(OpCodes.Ret);
    }

    private void EmitReturnValueAndLeave()
    {
        if (_returnEmissionFrame is not { } frame)
        {
            _il.Emit(OpCodes.Ret);
            return;
        }

        if (frame.ValueLocal is null)
        {
            // Void method contexts still evaluate a source return value for
            // side effects, then discard it before leaving the method body.
            _il.Emit(OpCodes.Pop);
        }
        else
        {
            _il.Emit(OpCodes.Stloc, frame.ValueLocal);
        }

        _il.Emit(OpCodes.Leave, frame.Epilogue);
    }

    private void EmitReturnWithoutValueAndLeave()
    {
        if (_returnEmissionFrame is not { } frame)
        {
            if (_blockOutputLocal is not null)
            {
                _il.Emit(OpCodes.Ldloc, _blockOutputLocal);
            }
            _il.Emit(OpCodes.Ret);
            return;
        }

        _il.Emit(OpCodes.Leave, frame.Epilogue);
    }

    private bool TryEmitDeferredCleanupReturn(BoundReturnStatement statement)
    {
        if (_deferredCleanupFrames.Count == 0)
        {
            return false;
        }

        // The interpreter evaluates the return pipeline before raising and
        // suppressing its control-flow signal. Preserve those side effects,
        // but discard the unused value and stop only this cleanup body.
        if (statement.Value is not null)
        {
            var valueType = EmitPipelineAsValue(statement.Value);
            if (valueType is not null)
            {
                _il.Emit(OpCodes.Pop);
            }
        }

        _il.Emit(OpCodes.Leave, _deferredCleanupFrames.Peek().ExitLabel);
        return true;
    }

    private bool TryEmitDeferredCleanupLoopControl()
    {
        if (_deferredCleanupFrames.Count == 0)
        {
            return false;
        }

        var cleanup = _deferredCleanupFrames.Peek();
        if (_loopStack.Count > cleanup.OwningLoopDepth)
        {
            // The target loop was introduced inside this cleanup.
            return false;
        }

        _il.Emit(OpCodes.Leave, cleanup.ExitLabel);
        return true;
    }

    private LocalBuilder EmitCreateDeferFailureState()
    {
        var failureState = _il.DeclareLocal(s_deferFailureStateType);
        _il.Emit(OpCodes.Newobj, s_deferFailureStateCtor);
        _il.Emit(OpCodes.Stloc, failureState);
        return failureState;
    }

    private bool TryEmitSuppressedEcho(BoundPipeline pipeline)
    {
        if (pipeline.Stages.Count != 1
            || pipeline.BoundRedirections.Count > 0
            || pipeline.BoundInputRedirection is not null
            || pipeline.Stages[0] is not BoundCommandCall { Name: "echo" } echo)
        {
            return false;
        }

        // A deferred block discards pipeline output. For echo's simple
        // positional form, that means only its argument expressions need to
        // run; routing through InvokeValue would add an unnecessary Tier 2
        // host dependency to otherwise-pure compiled code.
        foreach (var argument in echo.Arguments)
        {
            if (argument.Name is not null || argument.IsSplat)
            {
                return false;
            }
        }

        foreach (var argument in echo.Arguments)
        {
            var argumentType = EmitExpression(argument.Value);
            if (argumentType is not null)
            {
                _il.Emit(OpCodes.Pop);
            }
        }
        return true;
    }

    private void EmitDeferredRemainder(
        IReadOnlyList<BoundStatement> statements,
        int nextIndex,
        BoundDeferStatement defer,
        LocalBuilder failureState,
        bool isOutermostDefer)
    {
        _il.BeginExceptionBlock();
        EmitBlockStatementsWithDefers(
            statements,
            nextIndex,
            failureState,
            isOutermostDefer: false);

        if (isOutermostDefer)
        {
            _il.BeginCatchBlock(typeof(Exception));
            var bodyFailure = _il.DeclareLocal(typeof(Exception));
            _il.Emit(OpCodes.Stloc, bodyFailure);
            _il.Emit(OpCodes.Ldloc, failureState);
            _il.Emit(OpCodes.Ldloc, bodyFailure);
            _il.Emit(OpCodes.Callvirt, s_deferCaptureBodyFailure);
            // Preserve the original exceptional exit when cleanup succeeds.
            // A cleanup failure thrown from the finally below supersedes this
            // rethrow with ToshDeferAggregateException via the shared state.
            _il.Emit(OpCodes.Rethrow);
        }

        _il.BeginFinallyBlock();
        EmitCapturedDeferredCleanup(defer.Body, failureState);
        if (isOutermostDefer)
        {
            // Inner cleanups have already appended their failures to this
            // shared state. Throw once, after the final (earliest registered)
            // cleanup has run, so cleanup-only failures stay classified as
            // cleanup failures and preserve actual LIFO order.
            _il.Emit(OpCodes.Ldloc, failureState);
            _il.Emit(OpCodes.Callvirt, s_deferThrowIfCleanupFailed);
        }
        _il.EndExceptionBlock();
    }

    private void EmitCapturedDeferredCleanup(
        BoundBlock body,
        LocalBuilder failureState)
    {
        var cleanupExit = _il.DefineLabel();

        _il.BeginExceptionBlock();
        _deferredCleanupFrames.Push(
            new DeferredCleanupEmissionFrame(cleanupExit, _loopStack.Count));
        try
        {
            EmitDeferredBlock(body);
        }
        finally
        {
            _deferredCleanupFrames.Pop();
        }

        _il.BeginCatchBlock(typeof(Exception));
        var cleanupFailure = _il.DeclareLocal(typeof(Exception));
        _il.Emit(OpCodes.Stloc, cleanupFailure);
        _il.Emit(OpCodes.Ldloc, failureState);
        _il.Emit(OpCodes.Ldloc, cleanupFailure);
        _il.Emit(OpCodes.Callvirt, s_deferCaptureCleanupFailure);
        _il.EndExceptionBlock();

        _il.MarkLabel(cleanupExit);
    }

    private static BoundBlock CreateSyntheticBlock(
        IReadOnlyList<BoundStatement> statements,
        TextSpan fallbackSpan)
    {
        if (statements.Count == 0)
        {
            return new BoundBlock(statements, fallbackSpan);
        }

        return new BoundBlock(
            statements,
            TextSpan.FromBounds(
                statements[0].Span.Start,
                statements[^1].Span.End));
    }
}
