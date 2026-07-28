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
        _il.Emit(OpCodes.Ldstr, frameName);
        _il.Emit(OpCodes.Call, s_hostEnterExecutionFrame);
        _il.Emit(OpCodes.Stloc, lease);
        _il.BeginExceptionBlock();
        return lease;
    }

    private void EmitExecutionFrameExit(LocalBuilder lease)
    {
        _il.BeginFinallyBlock();
        _il.Emit(OpCodes.Ldloc, lease);
        _il.Emit(OpCodes.Callvirt, s_executionFrameDispose);
        _il.EndExceptionBlock();
    }
}
