using System.Collections;
using System.Runtime.InteropServices;

using Tosh.Runtime;

namespace Tosh.Stdlib.Clr;

[CommandCategory("CLR")]
[CommandArgument("buffer|pointer", "NativeBuffer or pointer to write into.")]
[CommandArgument("value", "String, byte sequence, enum, primitive, pointer-sized value, or struct-layout value to write.")]
[CommandArgument("offset", "Optional byte offset from the buffer or pointer before writing.", Required = false, TypeName = "int")]
[CommandExample("native-write $buffer \"hello\"", Title = "Write a C string")]
[CommandExample("native-write $buffer [72 105 0] 0", Title = "Write explicit bytes")]
[CommandOutput("Emits nothing; writes the supplied value(s) into the native buffer as a side effect.")]
public sealed class NativeWriteCommand : ShellCommand
{
    public NativeWriteCommand(string name = "native-write")
        : base(name, "Writes a C string, byte sequence, or struct-layout value into native memory.", $"{name} <buffer|pointer> <value> [offset]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count < 2 || context.Arguments.Count > 3)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.native_write_argument_count",
                title: "native-write expects a target, a value, and an optional offset.",
                label: "write 'native-write <buffer|pointer> <value> [offset]'");
        }

        var target = context.Arguments[0];
        var value = context.Arguments[1];
        var offset = context.Arguments.Count == 3
            ? ReadOffset(context, 2)
            : 0;

        if (target is NativeBuffer buffer)
        {
            WriteToBuffer(context, buffer, value, offset);
            await Task.CompletedTask;
            yield break;
        }

        var pointer = IntPtr.Add(NativeCommandUtilities.ResolvePointer(context, target, 0), offset);
        WriteToPointer(context, pointer, value, 1);
        await Task.CompletedTask;
        yield break;
    }

    private static void WriteToBuffer(CommandContext context, NativeBuffer buffer, object? value, int offset)
    {
        if (value is string text)
        {
            buffer.WriteCString(text, offset);
            return;
        }

        if (TryGetByteSequence(value, out var byteSequence))
        {
            buffer.WriteBytes(byteSequence, offset);
            return;
        }

        WriteStructuredValue(context, IntPtr.Add(buffer.Pointer, offset), value, 1);
    }

    private static void WriteToPointer(CommandContext context, IntPtr pointer, object? value, int argumentIndex)
    {
        if (value is string text)
        {
            var bytes = NativeInteropUtilities.EncodeCString(text);
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            return;
        }

        if (TryGetByteSequence(value, out var byteSequence))
        {
            Marshal.Copy(byteSequence, 0, pointer, byteSequence.Length);
            return;
        }

        WriteStructuredValue(context, pointer, value, argumentIndex);
    }

    private static void WriteStructuredValue(CommandContext context, IntPtr pointer, object? value, int argumentIndex)
    {
        if (value is null)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.native_write_requires_value",
                title: "native-write requires a non-null value.",
                argumentIndex: argumentIndex,
                label: "write a string, byte sequence, enum, primitive, or struct-layout value here");
        }

        var runtimeType = value.GetType();

        if (runtimeType.IsEnum ||
            runtimeType.IsPrimitive ||
            runtimeType == typeof(IntPtr) ||
            runtimeType == typeof(UIntPtr) ||
            NativeInteropUtilities.IsStructLayoutType(runtimeType))
        {
            Marshal.StructureToPtr(value, pointer, fDeleteOld: false);
            return;
        }

        throw context.CreateDiagnostic(
            code: "tosh.runtime.native_write_unsupported_value",
            title: $"native-write does not know how to write values of type '{runtimeType.Name}'.",
            argumentIndex: argumentIndex,
            label: "use a string, byte sequence, enum, primitive, or struct-layout value here");
    }

    private static bool TryGetByteSequence(object? value, out byte[] bytes)
    {
        if (value is byte[] byteArray)
        {
            bytes = byteArray;
            return true;
        }

        if (value is IEnumerable enumerable && value is not string)
        {
            var items = new List<byte>();

            foreach (var item in enumerable)
            {
                if (!TypeConversion.TryConvert(item, typeof(byte), out var converted) ||
                    converted is not byte byteValue)
                {
                    bytes = Array.Empty<byte>();
                    return false;
                }

                items.Add(byteValue);
            }

            bytes = items.ToArray();
            return true;
        }

        bytes = Array.Empty<byte>();
        return false;
    }

    private static int ReadOffset(CommandContext context, int argumentIndex)
    {
        if (TypeConversion.TryConvert(context.Arguments[argumentIndex], typeof(int), out var converted) &&
            converted is int value)
        {
            return value;
        }

        throw context.CreateDiagnostic(
            code: "tosh.runtime.native_write_offset_requires_int",
            title: "native-write offsets must be integers.",
            argumentIndex: argumentIndex,
            label: "write an integer offset here");
    }
}
