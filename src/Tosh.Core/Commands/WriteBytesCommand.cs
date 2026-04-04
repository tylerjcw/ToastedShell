namespace Tosh.Core.Commands;

public sealed class WriteBytesCommand : ShellCommand
{
    public WriteBytesCommand()
        : base("write-bytes", "Writes byte-oriented content to a file, replacing any previous contents.", "write-bytes <path> [bytes...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var path = FileIoUtilities.ResolveRequiredPath(context, 0);
        var bytes = await FileIoUtilities.ReadBytePayloadAsync(context, CommandArguments.Slice(context.Arguments, 1));

        await FileIoUtilities.WriteAllBytesAsync(path, bytes, context.CancellationToken);
        yield return FileSystemEntry.From(new FileInfo(path));
    }
}
