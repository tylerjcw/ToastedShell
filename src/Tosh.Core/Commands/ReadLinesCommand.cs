namespace Tosh.Core.Commands;

[CommandCategory("Filesystem")]
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
