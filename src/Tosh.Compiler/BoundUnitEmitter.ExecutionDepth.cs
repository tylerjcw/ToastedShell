using System.Reflection;
using System.Reflection.Emit;

namespace Tosh.Compiler;

internal sealed partial class EmitterImpl
{
    private static readonly MethodInfo s_executionFrameDispose =
        typeof(IDisposable).GetMethod(nameof(IDisposable.Dispose), Type.EmptyTypes)!;

    /// <summary>
    /// Starts a protected compiled execution frame.  The paired
    /// <see cref="EmitExecutionFrameExit"/> must be emitted before the
    /// method's return epilogue so source <c>return</c> instructions can use
    /// their existing <c>leave</c>-to-epilogue path and still execute this
    /// finally handler.
    /// </summary>
    private LocalBuilder EmitExecutionFrameEntry(string frameName)
    {
        var lease = _il.DeclareLocal(typeof(IDisposable));
        EmitEnterExecutionFrameCall(_il, frameName);
        _il.Emit(OpCodes.Stloc, lease);
        _il.BeginExceptionBlock();
        return lease;
    }

    /// <summary>
    /// Emits the recursion-guard call, choosing the host wrapper or the
    /// <c>Tosh.Runtime</c> primitive according to the profile (<c>TS-P1-25</c>).
    /// </summary>
    /// <remarks>
    /// A pure artifact must carry no reference to <c>Tosh.Compiler.Runtime</c>,
    /// and this call was one of three places the emitter wrote one
    /// unconditionally. The primitive takes the depth limit as an argument
    /// where the host wrapper reads it from session configuration, so the pure
    /// form passes the documented default.
    /// </remarks>
    private void EmitEnterExecutionFrameCall(ILGenerator il, string frameName)
    {
        if (_profile == CompileProfile.Pure)
        {
            // `TOAST-0049`. Read at run time, because the limit is derived from the stack
            // the process was given and a literal would freeze the compiling machine's.
            il.Emit(OpCodes.Call, s_guardDefaultMaximumDepth);
            il.Emit(OpCodes.Ldstr, frameName);
            il.Emit(OpCodes.Ldnull);                     // sourceName
            il.Emit(OpCodes.Ldnull);                     // sourceText
            EmitNullTextSpan(il);                        // span
            il.Emit(OpCodes.Call, s_guardEnterExecutionFrame);
            return;
        }

        il.Emit(OpCodes.Ldstr, frameName);
        il.Emit(OpCodes.Call, s_hostEnterExecutionFrame);
    }

    /// <summary>Pushes a <c>TextSpan?</c> with no value.</summary>
    private static void EmitNullTextSpan(ILGenerator il)
    {
        var local = il.DeclareLocal(typeof(global::Tosh.Runtime.TextSpan?));
        il.Emit(OpCodes.Ldloca, local);
        il.Emit(OpCodes.Initobj, typeof(global::Tosh.Runtime.TextSpan?));
        il.Emit(OpCodes.Ldloc, local);
    }

    private void EmitExecutionFrameExit(LocalBuilder lease)
    {
        _il.BeginFinallyBlock();
        _il.Emit(OpCodes.Ldloc, lease);
        _il.Emit(OpCodes.Callvirt, s_executionFrameDispose);
        _il.EndExceptionBlock();
    }
}
