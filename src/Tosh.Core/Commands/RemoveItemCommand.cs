namespace Tosh.Core.Commands;

public sealed class RemoveItemCommand : ShellCommand
{
    public RemoveItemCommand()
        : base("rm", "Removes files or directories.", "rm [-r] [-f] <path> [path...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var recursive = parsed.HasFlag("r", "R", "recursive");
        var force = parsed.HasFlag("f", "force");
        var paths = await ShellPathArguments.CollectAsync(context, parsed.Positionals, context.CancellationToken);

        if (paths.Count == 0)
        {
            throw new InvalidOperationException("rm requires at least one path or pipeline input.");
        }

        foreach (var path in paths)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(path))
            {
                var file = new FileInfo(path);
                file.Delete();
                yield return file;
                continue;
            }

            if (Directory.Exists(path))
            {
                if (!recursive)
                {
                    throw new InvalidOperationException($"'{path}' is a directory. Use -r to remove directories.");
                }

                var directory = new DirectoryInfo(path);
                directory.Delete(recursive: true);
                yield return directory;
                continue;
            }

            if (!force)
            {
                throw new InvalidOperationException($"Path '{path}' does not exist.");
            }
        }
    }
}
