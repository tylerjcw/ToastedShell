namespace Tosh.Core.Commands;

[CommandCategory("Filesystem")]
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
