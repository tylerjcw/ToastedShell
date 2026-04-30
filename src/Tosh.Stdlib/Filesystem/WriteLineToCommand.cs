using Tosh.Runtime;

namespace Tosh.Stdlib.Filesystem;

[Stdlib(StdlibCategory.Filesystem)]
[CommandCategory("Filesystem")]
[CommandArgument("handle", "The managed text file handle to write into.")]
[CommandArgument("value ...", "Optional explicit values to write as one line. When omitted, each pipeline value becomes its own line.", Required = false)]
[CommandExample("write-line-to $handle hello world")]
[CommandExample("echo alpha beta | write-line-to $handle")]
[CommandOutput("Writes line-oriented text into the handle and does not emit pipeline output.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "When no explicit values are supplied, each pipeline value is written as its own line.")]
[CommandNote("These commands work with managed file handles returned by `open-file` or by `FileSystemEntry` methods like `OpenText()` and `OpenRead()`. `seek` returns the handle so you can keep piping through the stream workflow, while `copy-to` copies from one compatible handle into another.")]
public sealed class WriteLineToCommand : ShellCommand
{
    public WriteLineToCommand(string name = "write-line-to")
        : base(name, "Writes one or more text lines to an open managed text file handle.", $"{name} <handle> [value...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count == 0)
        {
            throw new InvalidOperationException($"{Name} requires a file handle as its first argument.");
        }

        var handle = StreamCommandUtilities.ResolveHandle(context.Arguments[0]);

        if (handle.IsBinary)
        {
            throw new InvalidOperationException($"{Name} only works with text file handles.");
        }

        var values = CommandArguments.Slice(context.Arguments, 1);

        if (values.Count > 0)
        {
            var rendered = await FileIoUtilities.RenderTextPayloadAsync(context, values);
            handle.WriteTextLine(rendered);
            yield break;
        }

        var inputValues = await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);

        if (inputValues.Count == 0)
        {
            handle.WriteTextLine(string.Empty);
            yield break;
        }

        foreach (var item in inputValues)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            handle.WriteTextLine(ExternalTextSerializer.Serialize(item));
        }
    }
}
