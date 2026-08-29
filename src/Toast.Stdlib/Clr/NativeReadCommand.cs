using System.Runtime.InteropServices;

using Tosh.Runtime;

namespace Tosh.Stdlib.Clr;

[CommandCategory("CLR")]
[CommandArgument("cstring|bytes|type-name", "Read mode: a null-terminated C string, a byte array, or a supported native scalar/struct-layout type.")]
[CommandArgument("buffer|pointer", "NativeBuffer or pointer to read from. May be supplied from the pipeline.", Required = false)]
[CommandArgument("--at", "Byte offset from the buffer or pointer before reading.", Required = false, TypeName = "int")]
[CommandArgument("--count", "How many to read. Bytes when the mode is `bytes`, where it is required; otherwise elements of the named type, and a single value is read without it.", Required = false, TypeName = "int")]
[CommandExample("$buffer | native-read cstring", Title = "Read a C string from a native buffer")]
[CommandExample("native-read bytes $buffer --count 16", Title = "Read a byte range")]
[CommandExample("native-read int32 $buffer --at 4", Title = "Read an Int32 at an offset")]
[CommandOutput("The decoded value(s) read from the native buffer, in the requested format.")]
public sealed class NativeReadCommand : ShellCommand
{
    public NativeReadCommand(string name = "native-read")
        : base(name, "Reads a C string, byte range, or native scalar/struct-layout value from native memory.", $"{name} <cstring|bytes|type-name> [buffer|pointer] [--at <offset>] [--count <bytes>]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count == 0)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.native_read_requires_mode",
                title: "native-read needs a read mode or type name.",
                label: "write 'cstring', 'bytes', or a native type name");
        }

        var mode = context.Arguments[0]?.ToString()?.Trim();

        if (string.IsNullOrWhiteSpace(mode))
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.native_read_requires_mode",
                title: "native-read needs a read mode or type name.",
                argumentIndex: 0,
                label: "write 'cstring', 'bytes', or a native type name");
        }

        var options = ParseOptions(context);
        var sources = await ResolveSourcesAsync(context, options);

        if (sources.Count == 0)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.native_read_requires_source",
                title: "native-read needs a native buffer or pointer source.",
                label: "pipe a source in or pass one as an argument");
        }

        foreach (var (source, argumentIndex) in sources)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            yield return ReadValue(context, mode, source, argumentIndex, options);
        }
    }

    private readonly record struct ReadOptions(int Offset, int? Count, IReadOnlyList<int> PositionalIndexes);

    /// <summary>
    /// Offset and count are named flags rather than positional slots.
    /// Positionally, the offset sat at argument 3 behind a `length` slot that
    /// only `bytes` mode reads — so every scalar read had to write a meaningless
    /// `0` to reach it (`read-buffer long $buf 0 8`).
    /// </summary>
    private static ReadOptions ParseOptions(CommandContext context)
    {
        var arguments = context.Arguments;
        var offset = 0;
        int? count = null;
        var positional = new List<int>();

        for (var index = 1; index < arguments.Count; index++)
        {
            var text = arguments[index]?.ToString();

            if (text is "--at" or "--offset")
            {
                offset = CommandArguments.RequireConverted<int>(arguments, ++index, "offset");
                continue;
            }

            if (text is "--count" or "--length")
            {
                count = CommandArguments.RequireConverted<int>(arguments, ++index, "count");
                continue;
            }

            positional.Add(index);
        }

        return new ReadOptions(offset, count, positional);
    }

    private static object? ReadValue(
        CommandContext context,
        string mode,
        object? source,
        int? argumentIndex,
        ReadOptions options)
    {
        var pointer = NativeCommandUtilities.ResolvePointer(context, source, argumentIndex ?? 1);
        pointer = IntPtr.Add(pointer, options.Offset);

        if (string.Equals(mode, "cstring", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "cstr", StringComparison.OrdinalIgnoreCase))
        {
            if (source is NativeBuffer buffer)
            {
                return buffer.ReadCString(options.Offset);
            }

            return pointer == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(pointer);
        }

        if (string.Equals(mode, "bytes", StringComparison.OrdinalIgnoreCase))
        {
            if (options.Count is not { } length)
            {
                throw context.CreateDiagnostic(
                    code: "tosh.runtime.native_read_requires_length",
                    title: "native-read bytes requires a byte count.",
                    argumentIndex: 0,
                    label: "write '--count <bytes>'");
            }

            NativeCommandUtilities.ValidateBufferRange(
                context, source, options.Offset, length, argumentIndex ?? 1, "native-read bytes");

            var bytes = new byte[length];
            Marshal.Copy(pointer, bytes, 0, length);
            return bytes;
        }

        var type = NativeCommandUtilities.ResolveInteropType(context, context.Arguments[0], 0, allowString: false);

        if (!NativeInteropUtilities.IsSupportedInteropType(type, allowString: false))
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.native_read_requires_native_type",
                title: $"native-read currently supports native scalar types, struct-layout types, 'cstring', or 'bytes', not '{mode}'.",
                argumentIndex: 0,
                label: "use a primitive, enum, pointer-sized, or struct-layout type here");
        }

        var stride = NativeInteropUtilities.SizeOf(type);

        // `TOAST-0079`. `--count` meant a byte count for `bytes` and was ignored for every
        // other type, so reading an array back was a loop of one command per element — the
        // mirror of the write side's problem. For a named type it is an *element* count, which
        // is what the same word means in `read-buffer bytes`: how many of the thing you asked
        // for.
        if (options.Count is { } count)
        {
            if (count < 0)
            {
                throw context.CreateDiagnostic(
                    code: "tosh.runtime.native_read_negative_count",
                    title: $"native-read {mode} cannot read a negative number of elements.",
                    argumentIndex: 0,
                    label: "use zero or a positive count");
            }

            NativeCommandUtilities.ValidateBufferRange(
                context, source, options.Offset, checked(count * stride), argumentIndex ?? 1,
                $"native-read {mode}");

            var values = new object?[count];

            for (var index = 0; index < count; index++)
            {
                values[index] = NativeInteropUtilities.ReadValue(type, IntPtr.Add(pointer, index * stride));
            }

            return values;
        }

        NativeCommandUtilities.ValidateBufferRange(
            context, source, options.Offset, stride, argumentIndex ?? 1, $"native-read {mode}");

        return NativeInteropUtilities.ReadValue(type, pointer);
    }

    private static async Task<List<(object? Source, int? ArgumentIndex)>> ResolveSourcesAsync(
        CommandContext context,
        ReadOptions options)
    {
        var sources = new List<(object?, int?)>();

        await using var enumerator = context.Input.GetAsyncEnumerator(context.CancellationToken);

        if (await enumerator.MoveNextAsync())
        {
            do
            {
                sources.Add((enumerator.Current, null));
            }
            while (await enumerator.MoveNextAsync());

            return sources;
        }

        // The first non-flag argument after the mode is the buffer.
        if (options.PositionalIndexes.Count > 0)
        {
            var index = options.PositionalIndexes[0];
            sources.Add((context.Arguments[index], index));
        }

        return sources;
    }
}
