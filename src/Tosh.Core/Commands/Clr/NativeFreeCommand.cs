namespace Tosh.Core.Commands.Clr;

[Stdlib(StdlibCategory.Clr)]
[CommandCategory("CLR")]
[CommandArgument("buffer ...", "Native buffers to free. Buffers may also be supplied from the pipeline.", Required = false)]
[CommandExample("$buffer | native-free", Title = "Free a piped native buffer")]
[CommandExample("native-free $a $b", Title = "Free explicit buffers")]
[CommandOutput("Emits nothing; releases the native buffer as a side effect.")]
public sealed class NativeFreeCommand : ShellCommand
{
    public NativeFreeCommand()
        : base("native-free", "Frees one or more native buffers allocated by alloc/native-alloc.", "native-free [buffer ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var freedAny = false;

        await using var enumerator = context.Input.GetAsyncEnumerator(context.CancellationToken);

        if (await enumerator.MoveNextAsync())
        {
            do
            {
                FreeBuffer(context, enumerator.Current, argumentIndex: null);
                freedAny = true;
            }
            while (await enumerator.MoveNextAsync());
        }
        else
        {
            for (var index = 0; index < context.Arguments.Count; index++)
            {
                FreeBuffer(context, context.Arguments[index], index);
                freedAny = true;
            }
        }

        if (!freedAny)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.native_free_requires_buffer",
                title: "native-free needs at least one native buffer.",
                label: "pipe a buffer in or pass one as an argument");
        }

        yield break;
    }

    private static void FreeBuffer(CommandContext context, object? value, int? argumentIndex)
    {
        if (value is not NativeBuffer buffer)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.native_free_requires_native_buffer",
                title: "native-free only accepts buffers created by native-alloc.",
                argumentIndex: argumentIndex,
                label: "pass a native buffer here");
        }

        buffer.Dispose();
    }
}
