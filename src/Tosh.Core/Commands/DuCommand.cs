namespace Tosh.Core.Commands;

public sealed class DuCommand : ShellCommand
{
    public DuCommand(string name = "du")
        : base(name, "Returns disk usage information for files and directories.", $"{name} [-a] [-s] [-d depth] [path ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var options = ParseOptions(context.Arguments);
        var paths = options.Paths.Count == 0
            ? [context.Runtime.CurrentDirectory]
            : options.Paths.Select(path => PathUtilities.ResolvePath(context.Runtime.CurrentDirectory, path)).ToArray();

        foreach (var path in paths)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(path))
            {
                var file = new FileInfo(path);

                if (options.IncludeFiles || options.Summarize)
                {
                    yield return new PathUsageInfo(file.Name, file.FullName, 0, IsDirectory: false, StorageSize.FromBytes(file.Length));
                }

                continue;
            }

            if (!Directory.Exists(path))
            {
                throw new InvalidOperationException($"Path '{path}' does not exist.");
            }

            foreach (var entry in GetDirectoryUsage(new DirectoryInfo(path), 0, options))
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                yield return entry;
            }
        }
    }

    private static IReadOnlyList<PathUsageInfo> GetDirectoryUsage(DirectoryInfo directory, int depth, DuOptions options)
    {
        var output = new List<PathUsageInfo>();
        var size = GetDirectoryUsageRecursive(directory, depth, options, output);

        if (options.Summarize)
        {
            return [new PathUsageInfo(directory.Name, directory.FullName, depth, IsDirectory: true, StorageSize.FromBytes(size))];
        }

        return output;
    }

    private static long GetDirectoryUsageRecursive(DirectoryInfo directory, int depth, DuOptions options, List<PathUsageInfo> output)
    {
        long totalBytes = 0;

        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null)
            {
                continue;
            }

            if (entry is FileInfo file)
            {
                totalBytes += file.Length;

                if (options.IncludeFiles && depth + 1 <= options.MaxDepth)
                {
                    output.Add(new PathUsageInfo(file.Name, file.FullName, depth + 1, IsDirectory: false, StorageSize.FromBytes(file.Length)));
                }

                continue;
            }

            if (entry is DirectoryInfo childDirectory)
            {
                var childSize = GetDirectoryUsageRecursive(childDirectory, depth + 1, options, output);
                totalBytes += childSize;

                if (depth + 1 <= options.MaxDepth)
                {
                    output.Add(new PathUsageInfo(childDirectory.Name, childDirectory.FullName, depth + 1, IsDirectory: true, StorageSize.FromBytes(childSize)));
                }
            }
        }

        if (depth <= options.MaxDepth)
        {
            output.Add(new PathUsageInfo(directory.Name, directory.FullName, depth, IsDirectory: true, StorageSize.FromBytes(totalBytes)));
        }

        return totalBytes;
    }

    private static DuOptions ParseOptions(IReadOnlyList<object?> arguments)
    {
        var includeFiles = false;
        var summarize = false;
        var maxDepth = int.MaxValue;
        var paths = new List<string>();

        for (var index = 0; index < arguments.Count; index++)
        {
            var text = arguments[index]?.ToString();

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            switch (text)
            {
                case "-a":
                case "--all":
                    includeFiles = true;
                    continue;
                case "-s":
                case "--summarize":
                    summarize = true;
                    maxDepth = 0;
                    continue;
                case "-d":
                case "--max-depth":
                    maxDepth = CommandArguments.RequireConverted<int>(arguments, ++index, "depth");
                    continue;
            }

            if (text.StartsWith("-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unsupported du option '{text}'.");
            }

            paths.Add(text);
        }

        return new DuOptions(includeFiles, summarize, maxDepth, paths);
    }

    private sealed record DuOptions(bool IncludeFiles, bool Summarize, int MaxDepth, IReadOnlyList<string> Paths);
}
