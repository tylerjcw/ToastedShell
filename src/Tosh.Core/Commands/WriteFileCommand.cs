namespace Tosh.Core.Commands;

[CommandCategory("Filesystem")]
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
