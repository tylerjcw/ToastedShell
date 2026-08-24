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
    private void EmitCommandCallStatement(BoundCommandCall call)
    {
        if (!string.Equals(call.Name, "echo", StringComparison.Ordinal))
        {
            EmitHostInvokeStatement(call);
            return;
        }

        // Echo's inlined Console.WriteLine fast path can't unfold a
        // splat at compile time (the runtime length isn't known).
        // Build the argument array via EmitArgsArray (which handles
        // the splat expansion) and route through EchoArgs so the
        // output matches the inlined "join with space" formatting,
        // not the registered echo command's table layout. Named
        // args still fall back to the host bridge.
        var hasSplat = false;
        foreach (var a in call.Arguments)
        {
            if (a.Name is not null)
            {
                EmitHostInvokeStatement(call);
                return;
            }
            if (a.IsSplat) hasSplat = true;
        }
        if (hasSplat)
        {
            if (!EmitArgsArray(call)) return;
            _il.Emit(OpCodes.Call, s_hostEchoArgs);
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
            _il.Emit(OpCodes.Call, s_formatValue);
            _il.Emit(OpCodes.Call, s_writeLineString);
            return;
        }

        // `TOAST-0067`. One value per argument, each on its own line — not one joined
        // string. `echo` is a value producer, and the interpreter yields an argument's worth
        // of value for each: `echo 1 2 | count` is 2. Joining made the compiled backend
        // disagree with the interpreter *and* with itself, since `echo $items` over a
        // two-element list already contributed two.
        for (var i = 0; i < call.Arguments.Count; i++)
        {
            var argType = EmitExpression(call.Arguments[i].Value);
            if (argType is null)
            {
                _il.Emit(OpCodes.Ldstr, "?");
            }
            else
            {
                BoxIfValueType(argType);
                _il.Emit(OpCodes.Call, s_formatValue);
            }

            _il.Emit(OpCodes.Call, s_writeLineString);
        }
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
        RequireTier(2, "command invocation (statement)");
        _il.Emit(OpCodes.Call, s_hostInvokeStatement);
        _il.Emit(OpCodes.Pop);
    }

    /// <summary>
    /// Emits a value-context dispatch through the runtime host shim.
    /// By default returns <see cref="object"/> as the unwrapped single
    /// value, the list when multiple were yielded, or null. A parenthesized
    /// subexpression selects the strict zero/one/many collapse instead.
    /// </summary>
    private Type? EmitHostInvokeValue(
        BoundCommandCall call,
        bool requireSingleSubexpressionValue = false)
    {
        if (!EmitHostArgs(call)) return null;
        RequireTier(2, "command invocation (value)");
        _il.Emit(
            OpCodes.Call,
            requireSingleSubexpressionValue
                ? s_hostInvokeSubexpressionValue
                : _emittingReturnValue
                    ? s_hostInvokeValueOrNothing
                    : s_hostInvokeValue);
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
        return EmitArgsArray(call);
    }

    /// <summary>
    /// Builds an <c>object[]</c> from <paramref name="call"/>'s
    /// arguments and leaves it on the eval stack. Picks one of two
    /// emission strategies:
    /// <list type="bullet">
    /// <item><b>Fast path</b> (no splat): <c>newarr object[N]</c>
    /// + <c>stelem.ref</c> in slot order.</item>
    /// <item><b>Splat path</b>: build a <see cref="List{T}"/>,
    /// add positional / named entries, expand splats via the host
    /// shim, then call <c>ToArray()</c>.</item>
    /// </list>
    /// Named entries are emitted as
    /// <c>new NamedArgument(name, value)</c> so commands see the
    /// same shape they get from the interpreter.
    /// </summary>
    private bool EmitArgsArray(BoundCommandCall call)
        => EmitArgsArrayCore(call.Name, call.Arguments);

    /// <summary>
    /// Same as <see cref="EmitArgsArray(BoundCommandCall)"/> but works against any
    /// argument list (used by <c>new TypeName(...)</c>, <c>$obj.method(...)</c>,
    /// <c>Lib.method(...)</c>). The <paramref name="diagnosticContext"/> appears
    /// in diagnostics produced by named-block / named-splat checks.
    /// </summary>
    private bool EmitArgsArrayCore(string diagnosticContext, IReadOnlyList<BoundArgument> arguments)
    {
        var hasSplat = false;
        foreach (var arg in arguments)
        {
            if (arg.IsSplat) { hasSplat = true; break; }
        }

        if (!hasSplat)
        {
            _il.Emit(OpCodes.Ldc_I4, arguments.Count);
            _il.Emit(OpCodes.Newarr, typeof(object));
            for (var i = 0; i < arguments.Count; i++)
            {
                _il.Emit(OpCodes.Dup);
                _il.Emit(OpCodes.Ldc_I4, i);
                if (!EmitOneArgValueCore(diagnosticContext, arguments[i])) return false;
                _il.Emit(OpCodes.Stelem_Ref);
            }
            return true;
        }

        // Splat path: List<object?> + ToArray(), so the array
        // length isn't known until runtime.
        _il.Emit(OpCodes.Newobj, s_listCtor);
        foreach (var arg in arguments)
        {
            if (arg.IsSplat)
            {
                if (arg.Name is not null)
                {
                    Diagnostics.Add(
                        $"{diagnosticContext}: named splat arguments are not allowed");
                    return false;
                }
                if (arg.Value is BoundBlockExpression)
                {
                    Diagnostics.Add(
                        $"{diagnosticContext}: cannot splat a block expression");
                    return false;
                }
                _il.Emit(OpCodes.Dup);
                var t = EmitExpression(arg.Value);
                if (t is null) return false;
                BoxIfValueType(t);
                _il.Emit(OpCodes.Call, s_hostSpreadArgs);
                continue;
            }

            _il.Emit(OpCodes.Dup);
            if (!EmitOneArgValueCore(diagnosticContext, arg)) return false;
            _il.Emit(OpCodes.Callvirt, s_listAdd);
        }
        _il.Emit(OpCodes.Callvirt, s_listToArray);
        return true;
    }

    /// <summary>
    /// Emits the value for a single (non-splat) argument and leaves
    /// it on the eval stack as <see cref="object"/>. Block
    /// expressions are materialized via
    /// <see cref="EmitMakeBlock"/>; named arguments are wrapped in
    /// a fresh <see cref="global::Tosh.Language.NamedArgument"/>;
    /// everything else is the boxed expression value.
    /// </summary>
    private bool EmitOneArgValue(BoundCommandCall call, BoundArgument arg)
        => EmitOneArgValueCore(call.Name, arg);

    private bool EmitOneArgValueCore(string diagnosticContext, BoundArgument arg)
    {
        if (arg.Value is BoundBlockExpression block)
        {
            if (arg.Name is not null)
            {
                Diagnostics.Add(
                    $"{diagnosticContext}: named block arguments not yet supported");
                return false;
            }
            EmitMakeBlock(block);
            return true;
        }
        if (arg.Name is not null)
        {
            _il.Emit(OpCodes.Ldstr, arg.Name);
            var nt = EmitExpression(arg.Value);
            if (nt is null) return false;
            BoxIfValueType(nt);
            _il.Emit(OpCodes.Newobj, s_namedArgumentCtor);
            return true;
        }
        var t = EmitExpression(arg.Value);
        if (t is null) return false;
        BoxIfValueType(t);
        return true;
    }

    // ─── Expressions ──────────────────────────────────────────────

}
