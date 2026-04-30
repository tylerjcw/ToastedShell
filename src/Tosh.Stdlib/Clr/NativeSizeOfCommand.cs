using System.Runtime.InteropServices;

using Tosh.Runtime;

namespace Tosh.Stdlib.Clr;

[Stdlib(StdlibCategory.Clr)]
[CommandCategory("CLR")]
[CommandArgument("type-name ...", "One or more supported native interop type names.")]
[CommandExample("size-of int32", Title = "Size of a primitive native type")]
[CommandExample("size-of int32 double nint", Title = "Size several native types")]
[CommandOutput("An int — the size in bytes of the requested type or structure.")]
public sealed class NativeSizeOfCommand : ShellCommand
{
    public NativeSizeOfCommand(string name = "native-sizeof")
        : base(name, "Returns the unmanaged size of a supported native interop type.", $"{name} <type-name> [type-name ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count == 0)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.native_sizeof_requires_type",
                title: "native-sizeof requires at least one type name.",
                label: "write a CLR type or imported struct type here");
        }

        for (var index = 0; index < context.Arguments.Count; index++)
        {
            var type = NativeCommandUtilities.ResolveInteropType(context, context.Arguments[index], index, allowString: false);
            yield return Marshal.SizeOf(type);
        }

        await Task.CompletedTask;
    }
}
