using Tosh.Runtime;

namespace Tosh.Stdlib.Filesystem;

[CommandCategory("Filesystem")]
[CommandArgument("path", "The file path to append to.", TypeName = "path-like")]
[CommandArgument("value ...", "Optional explicit text values to append. When omitted, pipeline input becomes the appended text.", Required = false)]
[CommandExample("append-file ./notes.txt \" more\"")]
[CommandExample("echo \"tail line\" | append-file ./notes.txt")]
[CommandNote("These commands use ToSh's plain-text serialization rules, not the rich table renderer, so they are safe for intentional file output.")]
[CommandOutput("Returns the resulting filesystem entry for the written file.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "When no explicit value arguments are supplied, pipeline values are rendered as plain text and appended to the file.")]
public sealed class AppendFileCommand : ShellCommand
{
    public AppendFileCommand()
        : base("append-file", "Appends plain text to a file, creating it when needed.", "append-file <path> [value...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var path = FileIoUtilities.ResolveRequiredPath(context, 0);
        var text = await FileIoUtilities.RenderTextPayloadAsync(context, CommandArguments.Slice(context.Arguments, 1));

        await FileIoUtilities.AppendAllTextAsync(path, text, context.CancellationToken);
        yield return FileSystemEntry.From(new FileInfo(path));
    }
}
