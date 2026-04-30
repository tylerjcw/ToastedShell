namespace Tosh.Core.Commands;

[Stdlib(StdlibCategory.Filesystem)]
[CommandCategory("Filesystem")]
[CommandArgument("path", "The file path to create or replace.", TypeName = "path-like")]
[CommandArgument("bytes ...", "Optional explicit byte-oriented values. When omitted, pipeline input becomes the byte payload.", Required = false)]
[CommandExample("write-bytes ./data.bin [1, 2, 3, 255]")]
[CommandExample("read-bytes ./source.bin | write-bytes ./copy.bin")]
[CommandNote("Write-bytes accepts raw byte arrays, byte-like collections, and byte-convertible scalar values. Strings are encoded as UTF-8 in this first slice.")]
[CommandOutput("Returns the resulting filesystem entry for the written file.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "When no explicit byte values are supplied, pipeline input is converted into a byte payload.")]
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
