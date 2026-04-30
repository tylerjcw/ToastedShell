using Tosh.Runtime;

namespace Tosh.Stdlib.Filesystem;

[CommandCategory("Filesystem")]
[CommandArgument("path", "The file path to create or replace.", TypeName = "path-like")]
[CommandArgument("value ...", "Optional explicit text values to write. When omitted, pipeline input becomes the file body.", Required = false)]
[CommandExample("write-file ./notes.txt \"hello world\"")]
[CommandExample("echo alpha beta | write-file ./notes.txt")]
[CommandOutput("Returns the resulting filesystem entry for the written file.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "When no explicit value arguments are supplied, pipeline values are rendered as plain text and written to the file.")]
[CommandNote("These commands use ToSh's plain-text serialization rules, not the rich table renderer, so they are safe for intentional file output.")]
public sealed class WriteFileCommand : ShellCommand
{
    public WriteFileCommand()
        : base("write-file", "Writes plain text to a file, replacing any previous contents.", "write-file <path> [value...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var path = FileIoUtilities.ResolveRequiredPath(context, 0);
        var text = await FileIoUtilities.RenderTextPayloadAsync(context, CommandArguments.Slice(context.Arguments, 1));

        await FileIoUtilities.WriteAllTextAsync(path, text, context.CancellationToken);
        yield return FileSystemEntry.From(new FileInfo(path));
    }
}
