namespace Tosh.Core.Commands;

public sealed class ListDirectoryCommand : ShellCommand
{
    public ListDirectoryCommand()
        : base("ls", "Lists file system entries.", "ls [-a] [-l] [path]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var showHidden = parsed.HasFlag("a", "all");
        var preferLongDisplay = parsed.HasFlag("l", "long");
        var path = parsed.Positionals.Count == 0
            ? context.Runtime.CurrentDirectory
            : PathUtilities.ResolvePath(
                context.Runtime.CurrentDirectory,
                CommandArguments.RequireString(parsed.Positionals, 0, "path"));

        if (Directory.Exists(path))
        {
            var directory = new DirectoryInfo(path);
            IEnumerable<FileSystemInfo> rawEntries;

            try
            {
                rawEntries = directory.EnumerateFileSystemInfos();
            }
            catch (UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"Permission denied: '{path}'.");
            }

            var entries = rawEntries
                .Where(entry => showHidden || !entry.Name.StartsWith('.'))
                .OrderBy(entry => entry is FileInfo)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var entry in entries)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                yield return FileSystemEntry.From(entry, preferLongDisplay);
            }

            yield break;
        }

        if (File.Exists(path))
        {
            yield return FileSystemEntry.From(new FileInfo(path), preferLongDisplay);
            yield break;
        }

        throw new InvalidOperationException($"Path '{path}' does not exist.");
    }
}
