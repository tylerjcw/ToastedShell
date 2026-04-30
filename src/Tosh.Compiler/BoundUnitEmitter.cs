using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Tosh.Language.Binding;
using Tosh.Language.Binding.BoundNodes;

namespace Tosh.Compiler;

/// <summary>
/// Walking-skeleton IL emitter for Tosh's bound IR. The first goal
/// is the smallest end-to-end path: take a <see cref="BoundUnit"/>
/// containing literal-argument <c>echo</c> calls and emit a runnable
/// .NET assembly whose <c>Main</c> writes those literals to stdout.
///
/// Coverage is deliberately tiny:
///   • <c>BoundScript</c> with <c>BoundPipelineStatement</c> children
///   • each pipeline must be a single <see cref="BoundCommandCall"/>
///   • the call name must be <c>echo</c>
///   • each argument must be a <see cref="BoundLiteral"/> of int,
///     long, double, bool, or string
///
/// Anything outside this surface is reported via
/// <see cref="EmitResult"/> as a list of unsupported-shape diagnostics
/// rather than thrown — the caller can decide whether to fall back to
/// the tree-walking evaluator for the unsupported parts.
/// </summary>
public static class BoundUnitEmitter
{
    public static EmitResult Emit(BoundUnit unit, string assemblyName, Stream output)
    {
        var diagnostics = new List<string>();

        var coreAssembly = typeof(object).Assembly;
        var ab = new PersistedAssemblyBuilder(new AssemblyName(assemblyName), coreAssembly);
        var module = ab.DefineDynamicModule("MainModule");
        var program = module.DefineType(
            $"{assemblyName}.Program",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Abstract);

        var mainBuilder = program.DefineMethod(
            "Main",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(void),
            new[] { typeof(string[]) });

        var il = mainBuilder.GetILGenerator();

        var consoleWriteLineString = typeof(Console).GetMethod(
            nameof(Console.WriteLine),
            BindingFlags.Public | BindingFlags.Static,
            new[] { typeof(string) })!;

        var consoleWriteLineObject = typeof(Console).GetMethod(
            nameof(Console.WriteLine),
            BindingFlags.Public | BindingFlags.Static,
            new[] { typeof(object) })!;

        foreach (var statement in unit.Root.Statements)
        {
            EmitStatement(statement, il, diagnostics, consoleWriteLineString, consoleWriteLineObject);
        }

        il.Emit(OpCodes.Ret);

        program.CreateType();

        // Build a runnable PE with an entry point.
        var metadataBuilder = ab.GenerateMetadata(
            out var ilStream,
            out var mappedFieldData);

        var peHeaderBuilder = new PEHeaderBuilder(
            imageCharacteristics: Characteristics.ExecutableImage);

        var peBuilder = new ManagedPEBuilder(
            header: peHeaderBuilder,
            metadataRootBuilder: new MetadataRootBuilder(metadataBuilder),
            ilStream: ilStream,
            mappedFieldData: mappedFieldData,
            entryPoint: MetadataTokens.MethodDefinitionHandle(mainBuilder.MetadataToken));

        var peBlob = new BlobBuilder();
        peBuilder.Serialize(peBlob);
        peBlob.WriteContentTo(output);

        return new EmitResult(diagnostics);
    }

    private static void EmitStatement(
        BoundStatement statement,
        ILGenerator il,
        List<string> diagnostics,
        MethodInfo writeLineString,
        MethodInfo writeLineObject)
    {
        if (statement is not BoundPipelineStatement pipelineStmt)
        {
            diagnostics.Add($"unsupported statement: {statement.GetType().Name}");
            return;
        }

        var stages = pipelineStmt.Pipeline.Stages;
        if (stages.Count != 1 || stages[0] is not BoundCommandCall call)
        {
            diagnostics.Add($"unsupported pipeline shape (stages={stages.Count})");
            return;
        }

        if (!string.Equals(call.Name, "echo", StringComparison.Ordinal))
        {
            diagnostics.Add($"unsupported command in spike: '{call.Name}'");
            return;
        }

        // `echo a b c` joins arguments with spaces and writes one
        // line. For the spike we emit one WriteLine per argument
        // when there's a single arg, and a runtime String.Join when
        // there are multiple. To keep IL trivial in the single-arg
        // case (the only one tests need right now), we just
        // WriteLine the bare value.
        if (call.Arguments.Count == 0)
        {
            il.Emit(OpCodes.Ldstr, string.Empty);
            il.Emit(OpCodes.Call, writeLineString);
            return;
        }

        if (call.Arguments.Count == 1)
        {
            var arg = call.Arguments[0];
            if (!TryEmitLiteralAsObject(arg.Value, il, diagnostics))
            {
                return;
            }
            il.Emit(OpCodes.Call, writeLineObject);
            return;
        }

        // Multi-arg: build a string[] and call String.Join(" ", arr).
        var stringJoin = typeof(string).GetMethod(
            nameof(string.Join),
            new[] { typeof(string), typeof(string[]) })!;

        il.Emit(OpCodes.Ldstr, " ");
        il.Emit(OpCodes.Ldc_I4, call.Arguments.Count);
        il.Emit(OpCodes.Newarr, typeof(string));

        for (var i = 0; i < call.Arguments.Count; i++)
        {
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4, i);
            if (!TryEmitLiteralAsString(call.Arguments[i].Value, il, diagnostics))
            {
                il.Emit(OpCodes.Ldstr, "?");
            }
            il.Emit(OpCodes.Stelem_Ref);
        }

        il.Emit(OpCodes.Call, stringJoin);
        il.Emit(OpCodes.Call, writeLineString);
    }

    /// <summary>
    /// Emits IL that leaves an <c>object</c>-typed value on the
    /// stack. Boxes value-type literals.
    /// </summary>
    private static bool TryEmitLiteralAsObject(BoundExpression expression, ILGenerator il, List<string> diagnostics)
    {
        if (expression is not BoundLiteral literal)
        {
            diagnostics.Add($"unsupported argument expression: {expression.GetType().Name}");
            return false;
        }

        switch (literal.Value)
        {
            case null:
                il.Emit(OpCodes.Ldnull);
                return true;

            case string s:
                il.Emit(OpCodes.Ldstr, s);
                return true;

            case bool b:
                il.Emit(b ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Box, typeof(bool));
                return true;

            case int i:
                il.Emit(OpCodes.Ldc_I4, i);
                il.Emit(OpCodes.Box, typeof(int));
                return true;

            case long l:
                il.Emit(OpCodes.Ldc_I8, l);
                il.Emit(OpCodes.Box, typeof(long));
                return true;

            case double d:
                il.Emit(OpCodes.Ldc_R8, d);
                il.Emit(OpCodes.Box, typeof(double));
                return true;

            default:
                diagnostics.Add($"unsupported literal type: {literal.Value.GetType().Name}");
                return false;
        }
    }

    /// <summary>
    /// Emits IL that leaves a <c>string</c> on the stack (calling
    /// <c>ToString</c> for non-string literals). Used in the
    /// multi-argument <c>echo</c> path.
    /// </summary>
    private static bool TryEmitLiteralAsString(BoundExpression expression, ILGenerator il, List<string> diagnostics)
    {
        if (!TryEmitLiteralAsObject(expression, il, diagnostics))
        {
            return false;
        }

        // If the value is already a string the boxing was a no-op
        // (Ldstr leaves a string on the stack, no Box emitted), so
        // we can just call object.ToString safely either way.
        var objectToString = typeof(object).GetMethod(
            nameof(object.ToString),
            Type.EmptyTypes)!;
        il.Emit(OpCodes.Callvirt, objectToString);
        return true;
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
