using System.Runtime.InteropServices;

namespace Tosh.Core.Commands;

[CommandCategory("CLR")]
public sealed class NativeReadCommand : ShellCommand
{
    public NativeReadCommand(string name = "native-read")
        : base(name, "Reads a C string, byte range, or native scalar/struct-layout value from native memory.", $"{name} <cstring|bytes|type-name> [buffer|pointer] [length] [offset]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count == 0)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::native_read_requires_mode",
                title: "native-read needs a read mode or type name.",
                label: "write 'cstring', 'bytes', or a native type name");
        }

        var mode = context.Arguments[0]?.ToString()?.Trim();

        if (string.IsNullOrWhiteSpace(mode))
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::native_read_requires_mode",
                title: "native-read needs a read mode or type name.",
                argumentIndex: 0,
                label: "write 'cstring', 'bytes', or a native type name");
        }

        var sources = await ResolveSourcesAsync(context);

        if (sources.Count == 0)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::native_read_requires_source",
                title: "native-read needs a native buffer or pointer source.",
                label: "pipe a source in or pass one as an argument");
        }

        foreach (var (source, argumentIndex) in sources)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            yield return ReadValue(context, mode, source, argumentIndex);
        }
    }

    private static object? ReadValue(CommandContext context, string mode, object? source, int? argumentIndex)
    {
        var pointer = NativeCommandUtilities.ResolvePointer(context, source, argumentIndex ?? 1);
        var valueArgumentStart = argumentIndex is null ? 1 : 2;
        var offset = TryReadIntArgument(context, valueArgumentStart + 1, defaultValue: 0);
        pointer = IntPtr.Add(pointer, offset);

        if (string.Equals(mode, "cstring", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "cstr", StringComparison.OrdinalIgnoreCase))
        {
            if (source is NativeBuffer buffer)
            {
                return buffer.ReadCString(offset);
            }

            return pointer == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(pointer);
        }

        if (string.Equals(mode, "bytes", StringComparison.OrdinalIgnoreCase))
        {
            var length = TryReadRequiredIntArgument(context, valueArgumentStart);
            var bytes = new byte[length];
            Marshal.Copy(pointer, bytes, 0, length);
            return bytes;
        }

        var type = NativeCommandUtilities.ResolveInteropType(context, mode, 0, allowString: false);

        if (!NativeInteropUtilities.IsSupportedInteropType(type, allowString: false))
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::native_read_requires_native_type",
                title: $"native-read currently supports native scalar types, struct-layout types, 'cstring', or 'bytes', not '{mode}'.",
                argumentIndex: 0,
                label: "use a primitive, enum, pointer-sized, or struct-layout type here");
        }

        return NativeInteropUtilities.ReadValue(type, pointer);
    }

    private static async Task<List<(object? Source, int? ArgumentIndex)>> ResolveSourcesAsync(CommandContext context)
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

        if (context.Arguments.Count >= 2)
        {
            sources.Add((context.Arguments[1], 1));
        }

        return sources;
    }

    private static int TryReadRequiredIntArgument(CommandContext context, int index)
    {
        if (context.Arguments.Count <= index ||
            !TypeConversion.TryConvert(context.Arguments[index], typeof(int), out var converted) ||
            converted is not int value)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::native_read_requires_length",
                title: "native-read bytes requires a byte length.",
                argumentIndex: index,
                label: "write a byte length here");
        }

        return value;
    }

    private static int TryReadIntArgument(CommandContext context, int index, int defaultValue)
    {
        if (context.Arguments.Count <= index)
        {
            return defaultValue;
        }

        if (TypeConversion.TryConvert(context.Arguments[index], typeof(int), out var converted) &&
            converted is int value)
        {
            return value;
        }

        throw context.CreateDiagnostic(
            code: "tosh::runtime::native_read_offset_requires_int",
            title: "native-read offsets must be integers.",
            argumentIndex: index,
            label: "write an integer offset here");
    }
}
