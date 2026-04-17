namespace Tosh.Core.Commands;

[CommandCategory("Filesystem")]
[CommandArgument("path", "One or more files or directories to remove.", TypeName = "path-like")]
[CommandOption("-r", "Remove directories and their contents recursively.")]
[CommandOption("-f", "Ignore nonexistent files; never prompt.")]
[CommandOption("-i", "Prompt before each removal.")]
[CommandOption("-v", "Explain what is being done.")]
[CommandExample("rm temp.txt")]
[CommandExample("rm -rf build/", Title = "Force recursive delete")]
[CommandOutput("Returns a RemovalResult with total size and descendant tree.")]
[CommandSideEffects(WritesFiles = true)]
[PipelineInput(AcceptsList = true, Description = "Accepts piped path-like values.")]
public sealed class RemoveItemCommand : ShellCommand
{
    public RemoveItemCommand()
        : base("rm", "Removes files or directories.", "rm [-rfiv] <path> [path...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var recursive = parsed.HasFlag("r", "R", "recursive");
        var force = parsed.HasFlag("f", "force");
        var interactive = parsed.HasFlag("i", "interactive");
        var verbose = parsed.HasFlag("v", "verbose");
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
                if (interactive && !ConfirmRemoval(context, path))
                {
                    continue;
                }

                var file = new FileInfo(path);
                var size = StorageSize.FromBytes(file.Length);
                file.Delete();

                if (verbose)
                {
                    Console.Error.WriteLine($"removed '{path}'");
                }

                yield return new RemovalResult(fullName: path, isDirectory: false, size: size, children: []);
                continue;
            }

            if (Directory.Exists(path))
            {
                if (!recursive)
                {
                    throw new InvalidOperationException($"'{path}' is a directory. Use -r to remove directories.");
                }

                if (interactive && !ConfirmRemoval(context, path))
                {
                    continue;
                }

                var directory = new DirectoryInfo(path);
                var children = BuildDescendantTree(directory);
                var size = StorageSize.FromBytes(SumBytes(children));
                directory.Delete(recursive: true);

                if (verbose)
                {
                    Console.Error.WriteLine($"removed directory '{path}'");
                }

                yield return new RemovalResult(fullName: path, isDirectory: true, size: size, children: children);
                continue;
            }

            if (!force)
            {
                throw new InvalidOperationException($"Path '{path}' does not exist.");
            }
        }
    }

    private static bool ConfirmRemoval(CommandContext context, string path)
    {
        var provider = context.Runtime.InlinePrompts;

        if (provider is null)
        {
            return true;
        }

        var name = Path.GetFileName(path);
        return provider.Confirm($"rm: remove '{name}'?", false) ?? false;
    }

    private static IReadOnlyList<RemovedEntry> BuildDescendantTree(DirectoryInfo directory)
    {
        var children = new List<RemovedEntry>();

        foreach (var subDir in directory.EnumerateDirectories())
        {
            var grandchildren = BuildDescendantTree(subDir);
            children.Add(new RemovedEntry(subDir.Name, subDir.FullName, isDirectory: true, StorageSize.FromBytes(SumBytes(grandchildren)))
            {
                Children = grandchildren,
            });
        }

        foreach (var file in directory.EnumerateFiles())
        {
            children.Add(new RemovedEntry(file.Name, file.FullName, isDirectory: false, StorageSize.FromBytes(file.Length)));
        }

        return children;
    }

    private static long SumBytes(IReadOnlyList<RemovedEntry> entries)
    {
        var total = 0L;

        foreach (var entry in entries)
        {
            total += entry.Size.Bytes;

            if (entry.Children.Count > 0)
            {
                total += SumBytes(entry.Children);
            }
        }

        return total;
    }
}
