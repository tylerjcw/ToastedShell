using Tosh.Runtime;

namespace Tosh.Stdlib.Filesystem;

[CommandCategory("Filesystem")]
[CommandArgument("source", "The readable managed file handle to copy from.", Required = false)]
[CommandArgument("target", "The writable managed file handle to copy into.")]
[CommandExample("copy-to $source $target")]
[CommandExample("$source | copy-to $target")]
[CommandNote("These commands work with managed file handles returned by `open-file` or by `FileSystemEntry` methods like `OpenText()` and `OpenRead()`. `seek` returns the handle so you can keep piping through the stream workflow, while `copy-to` copies from one compatible handle into another.")]
[CommandOutput("Returns the number of bytes or text characters copied into the target handle.")]
[PipelineInput(AcceptsRecord = true, Description = "Consumes a piped source handle when you only pass the target handle explicitly.")]
public sealed class CopyToCommand : ShellCommand
{
    public CopyToCommand()
        : base("copy-to", "Copies the remaining contents of one managed file handle into another compatible handle.", "copy-to [source] <target>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var (source, target) = await ResolveHandlesAsync(context);
        yield return source.CopyTo(target);
    }

    private static async Task<(ManagedFileHandle Source, ManagedFileHandle Target)> ResolveHandlesAsync(CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Arguments.Count >= 2)
        {
            return (
                StreamCommandUtilities.ResolveHandle(context.Arguments[0]),
                StreamCommandUtilities.ResolveHandle(context.Arguments[1]));
        }

        if (context.Arguments.Count == 1)
        {
            var input = await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);

            if (input.Count == 0)
            {
                throw new InvalidOperationException("Copy-to expects a source file handle from the pipeline or as its first argument.");
            }

            if (input[0] is not ManagedFileHandle source)
            {
                throw new InvalidOperationException("Copy-to expects a file handle from the pipeline.");
            }

            return (source, StreamCommandUtilities.ResolveHandle(context.Arguments[0]));
        }

        throw new InvalidOperationException("Copy-to requires a source and target file handle.");
    }
}
