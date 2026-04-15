namespace Tosh.Core.Commands;

[CommandCategory("Filesystem")]
[CommandArgument("path ...", "One or more file paths to read line-by-line.", Required = false, TypeName = "path-like")]
[CommandExample("read-lines ./notes.txt")]
[CommandExample("read-lines ./notes.txt | grep error")]
[CommandOutput("Returns ShellTextLine values, one per line across the supplied files.")]
[PipelineInput(AcceptsList = true, Description = "Consumes piped path-like input when explicit file paths are omitted.")]
[CommandNote("These commands accept normal path-like values, including strings, FileInfo, and ToSh FileSystemEntry objects.")]
public sealed class ReadLinesCommand : ShellCommand
{
    public ReadLinesCommand()
        : base("read-lines", "Reads one or more files and emits their contents line-by-line.", "read-lines <path> [path...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var paths = await ShellPathArguments.CollectAsync(context, parsed.Positionals, context.CancellationToken);

        if (paths.Count == 0)
        {
            throw new InvalidOperationException("read-lines requires at least one path or pipeline input.");
        }

        var lines = await TextInputUtilities.ReadLinesFromFilesAsync(paths, context.CancellationToken);

        foreach (var line in lines)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            yield return new ShellTextLine(line.Text);
        }
    }
}
