using Tosh.Runtime;

namespace Tosh.Stdlib.Filesystem;

[Stdlib(StdlibCategory.Filesystem)]
[CommandCategory("Filesystem")]
[CommandArgument("path ...", "One or more file paths to read as raw byte arrays.", Required = false, TypeName = "path-like")]
[CommandExample("read-bytes ./image.bin")]
[CommandExample("read-bytes ./image.bin | type-of")]
[CommandNote("These commands accept normal path-like values, including strings, FileInfo, and ToSh FileSystemEntry objects.")]
[CommandOutput("Returns one byte-array value per file.")]
[PipelineInput(AcceptsList = true, Description = "Consumes piped path-like input when explicit file paths are omitted.")]
public sealed class ReadBytesCommand : ShellCommand
{
    public ReadBytesCommand()
        : base("read-bytes", "Reads one or more files and returns each file as a byte array.", "read-bytes <path> [path...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var paths = await ShellPathArguments.CollectAsync(context, parsed.Positionals, context.CancellationToken);

        if (paths.Count == 0)
        {
            throw new InvalidOperationException("read-bytes requires at least one path or pipeline input.");
        }

        foreach (var path in paths)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            FileIoUtilities.EnsureReadableFile(path, "read-bytes");
            yield return await File.ReadAllBytesAsync(path, context.CancellationToken);
        }
    }
}
