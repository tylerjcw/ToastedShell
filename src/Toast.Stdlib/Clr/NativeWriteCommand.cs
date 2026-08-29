using System.Collections;
using System.Runtime.InteropServices;

using Tosh.Runtime;

namespace Tosh.Stdlib.Clr;

[CommandCategory("CLR")]
[CommandArgument("buffer|pointer", "NativeBuffer or pointer to write into.")]
[CommandArgument("value", "String, byte sequence, enum, primitive, pointer-sized value, or struct-layout value to write.")]
[CommandArgument("--at", "Byte offset from the buffer or pointer before writing.", Required = false, TypeName = "int")]
[CommandArgument("--as", "Native interop type to write the value as, fixing the width. Without it the width comes from the value\u2019s own type, which is `Int32` for an integer only while it fits.", Required = false, TypeName = "string")]
[CommandExample("native-write $buffer \"hello\"", Title = "Write a C string")]
[CommandExample("native-write $buffer [72 105 0]", Title = "Write explicit bytes")]
[CommandExample("native-write $buffer 42 --at 8", Title = "Write at an offset")]
[CommandExample("native-write $buffer $n --as int32 --at 8", Title = "Write four bytes whatever $n is")]
[CommandOutput("Emits nothing; writes the supplied value(s) into the native buffer as a side effect.")]
public sealed class NativeWriteCommand : ShellCommand
{
    public NativeWriteCommand(string name = "native-write")
        : base(name, "Writes a C string, byte sequence, or struct-layout value into native memory.", $"{name} <buffer|pointer> <value> [--as <type>] [--at <offset>]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        // `--at` matches native-read's spelling. The trailing positional offset
        // stays accepted so existing scripts keep working.
        var offset = 0;
        object? writtenAs = null;
        var writtenAsIndex = 0;
        var positional = new List<object?>();

        for (var index = 0; index < context.Arguments.Count; index++)
        {
            var text = context.Arguments[index]?.ToString();

            if (text is "--at" or "--offset")
            {
                offset = CommandArguments.RequireConverted<int>(context.Arguments, ++index, "offset");
                continue;
            }

            // `TOAST-0077`. Without this the width is whatever the value's runtime type
            // happens to be, so a slot's size depends on the data that lands in it.
            if (text is "--as")
            {
                if (++index >= context.Arguments.Count)
                {
                    throw context.CreateDiagnostic(
                        code: "tosh.runtime.native_write_requires_type_name",
                        title: "--as needs a native interop type name.",
                        argumentIndex: index - 1,
                        label: "write something like '--as int32'");
                }

                writtenAs = context.Arguments[index];
                writtenAsIndex = index;
                continue;
            }

            positional.Add(context.Arguments[index]);
        }

        if (positional.Count < 2 || positional.Count > 3)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.native_write_argument_count",
                title: "native-write expects a target, a value, and an optional offset.",
                label: "write 'native-write <buffer|pointer> <value> [--at <offset>]'");
        }

        var target = positional[0];
        var value = positional[1];

        if (positional.Count == 3)
        {
            offset = ReadOffset(context, 2);
        }

        if (writtenAs is not null)
        {
            var elementType = NativeCommandUtilities.ResolveInteropType(context, writtenAs, writtenAsIndex);

            // `TOAST-0079`. A sequence with a stated element type is a bulk write: one command
            // for the whole array rather than one per element. Building a vertex buffer a
            // scalar at a time meant re-entering command dispatch for every number, which is
            // why `examples/gl_mouse_cube.tosh` compiles a display list instead of uploading
            // one. A string is not a sequence here — it already has a meaning.
            if (value is IEnumerable and not string)
            {
                WriteSequence(context, target, value, elementType, offset, writtenAsIndex);
                await Task.CompletedTask;
                yield break;
            }

            value = ConvertToWrittenType(context, value, elementType, writtenAsIndex);
        }

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

        // The string and byte-sequence paths above go through NativeBuffer, which
        // bounds-checks. A struct write did not: `Marshal.StructureToPtr` into an
        // undersized buffer corrupts the heap silently.
        if (value is not null)
        {
            var runtimeType = value.GetType();

            if (NativeInteropUtilities.IsSupportedInteropType(runtimeType, allowString: false))
            {
                NativeCommandUtilities.ValidateBufferRange(
                    context, buffer, offset, NativeInteropUtilities.SizeOf(runtimeType), 0, "native-write");
            }
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

    /// <summary>
    /// Converts a value to the type <c>--as</c> names, so the write is that many bytes wide —
    /// <c>TOAST-0077</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The width of a write is <c>Marshal.SizeOf(value.GetType())</c>, which made a buffer's
    /// layout a consequence of its data: an integer is <c>Int32</c> only while it fits, so a
    /// four-byte slot took eight bytes the first time a value arrived as a <c>long</c> — and
    /// the bounds check could not see it, because the write was inside the buffer. It
    /// overwrote the next slot and said nothing.
    /// </para>
    /// <para>
    /// A value that does not fit is refused rather than wrapped. Truncating here would replace
    /// a silent corruption of the neighbouring slot with a silent corruption of this one.
    /// </para>
    /// </remarks>
    private static object ConvertToWrittenType(
        CommandContext context,
        object? value,
        Type type,
        int argumentIndex)
    {
        if (value is not null && value.GetType() == type)
        {
            return value;
        }

        if (TypeConversion.TryConvert(value, type, out var converted) && converted is not null)
        {
            return converted;
        }

        throw context.CreateDiagnostic(
            code: "tosh.runtime.native_write_value_does_not_fit",
            title: $"'{value}' cannot be written as '{type.Name}'.",
            argumentIndex: argumentIndex,
            label: $"the value does not fit {NativeInteropUtilities.SizeOf(type)} byte(s) of '{type.Name}'",
            help: "widen the type this is written as, or narrow the value before writing it.");
    }

    /// <summary>
    /// Writes every element of a sequence at successive offsets — <c>TOAST-0079</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The stride is the stated element type's size, so the layout is decided once for the
    /// whole array rather than per value — the same reason <c>--as</c> exists for a single
    /// write (<c>TOAST-0077</c>), applied to the case that actually needs the throughput.
    /// </para>
    /// <para>
    /// The range is checked before anything is written, so a sequence too long for the buffer
    /// is refused whole rather than half-copied. That matters more here than for a scalar: a
    /// partial array upload leaves the buffer in a state no reader can detect.
    /// </para>
    /// </remarks>
    private static void WriteSequence(
        CommandContext context,
        object? target,
        object? value,
        Type elementType,
        int offset,
        int argumentIndex)
    {
        var elements = new List<object>();

        foreach (var item in (IEnumerable)value!)
        {
            elements.Add(ConvertToWrittenType(context, item, elementType, argumentIndex));
        }

        var stride = NativeInteropUtilities.SizeOf(elementType);

        NativeCommandUtilities.ValidateBufferRange(
            context, target, offset, checked(elements.Count * stride), argumentIndex, "native-write");

        var start = target is NativeBuffer buffer
            ? buffer.Pointer
            : NativeCommandUtilities.ResolvePointer(context, target, 0);

        for (var index = 0; index < elements.Count; index++)
        {
            Marshal.StructureToPtr(
                elements[index], IntPtr.Add(start, offset + (index * stride)), fDeleteOld: false);
        }
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
