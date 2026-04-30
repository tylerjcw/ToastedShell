using Tosh.Runtime;

namespace Tosh.Stdlib.Filesystem;

[Stdlib(StdlibCategory.Filesystem)]
[CommandCategory("Filesystem")]
[CommandArgument("handle", "The managed file handle to write into.")]
[CommandArgument("value ...", "Optional explicit values to write. When omitted, pipeline input becomes the written payload.", Required = false)]
[CommandExample("write-to $handle hello world")]
[CommandExample("echo alpha beta | write-to $handle")]
[CommandOutput("Writes into the handle and does not emit pipeline output.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "When no explicit values are supplied, pipeline values are written into the handle.")]
[CommandNote("These commands work with managed file handles returned by `open-file` or by `FileSystemEntry` methods like `OpenText()` and `OpenRead()`. `seek` returns the handle so you can keep piping through the stream workflow, while `copy-to` copies from one compatible handle into another.")]
public sealed class WriteToCommand : ShellCommand
{
    public WriteToCommand(string name = "write-to")
        : base(name, "Writes plain text or bytes to an open managed file handle.", $"{name} <handle> [value...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count == 0)
        {
            throw new InvalidOperationException($"{Name} requires a file handle as its first argument.");
        }

        var handle = StreamCommandUtilities.ResolveHandle(context.Arguments[0]);
        var values = CommandArguments.Slice(context.Arguments, 1);

        if (handle.IsBinary)
        {
            var bytes = await FileIoUtilities.ReadBytePayloadAsync(context, values);

            if (bytes.Length > 0)
            {
                handle.WriteBytes(bytes);
            }

            yield break;
        }

        var text = await FileIoUtilities.RenderTextPayloadAsync(context, values);

        if (text.Length > 0)
        {
            handle.WriteText(text);
        }
    }
}
