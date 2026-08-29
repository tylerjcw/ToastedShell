using Tosh.Runtime;

namespace Tosh.Stdlib.Clr;

[CommandCategory("CLR")]
[CommandArgument("bytes|type-name", "Byte count to allocate, or a native interop type name whose unmanaged size should be allocated.")]
[CommandExample("alloc 64", Title = "Allocate a 64-byte native buffer")]
[CommandExample("alloc int32", Title = "Allocate enough native memory for one Int32")]
// `TOAST-0077`. This said `IntPtr`, and returns a `NativeBuffer` — which carries the
// pointer, the length that makes the bounds checks possible, and disposal. The mismatch
// was silent except as a false `member_not_found` on `$buffer.Pointer`, for code that
// works.
[CommandOutput("A NativeBuffer wrapping the freshly allocated memory: `.Pointer` is the address and `.ByteLength` its size.", ClrType = typeof(NativeBuffer))]
public sealed class NativeAllocCommand : ShellCommand
{
    public NativeAllocCommand(string name = "native-alloc")
        : base(name, "Allocates a native unmanaged buffer by byte size or interop type.", $"{name} <bytes | type-name>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count != 1)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.native_alloc_argument_count",
                title: "native-alloc expects exactly one argument.",
                label: "pass a byte count or a struct/native type name");
        }

        var size = NativeCommandUtilities.ResolveAllocationSize(context, context.Arguments[0], 0);

        if (size < 0)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.native_alloc_negative_size",
                title: "native-alloc cannot allocate a negative number of bytes.",
                argumentIndex: 0,
                label: "use zero or a positive size");
        }

        yield return new NativeBuffer(size);
        await Task.CompletedTask;
    }
}
