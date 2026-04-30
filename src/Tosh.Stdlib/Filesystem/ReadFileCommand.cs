using Tosh.Runtime;

namespace Tosh.Stdlib.Filesystem;

[Stdlib(StdlibCategory.Filesystem)]
[CommandCategory("Filesystem")]
[CommandArgument("path ...", "One or more file paths to read as whole-text values.", Required = false, TypeName = "path-like")]
[CommandExample("read-file ./notes.txt")]
[CommandExample("ls *.md | first | read-file")]
[CommandOutput("Returns one string value per file.")]
[PipelineInput(AcceptsList = true, Description = "Consumes piped path-like input when explicit file paths are omitted.")]
[CommandNote("These commands accept normal path-like values, including strings, FileInfo, and ToSh FileSystemEntry objects.")]
public sealed class ReadFileCommand : ShellCommand
{
    public ReadFileCommand()
        : base("read-file", "Reads one or more files and returns each file as a single string value.", "read-file <path> [path...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var paths = await ShellPathArguments.CollectAsync(context, parsed.Positionals, context.CancellationToken);

        if (paths.Count == 0)
        {
            throw new InvalidOperationException("read-file requires at least one path or pipeline input.");
        }

        foreach (var path in paths)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            yield return await FileIoUtilities.ReadAllTextAsync(path, context.CancellationToken);
        }
    }
}
