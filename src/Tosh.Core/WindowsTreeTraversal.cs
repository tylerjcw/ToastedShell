namespace Tosh.Core;

/// <summary>
/// Native Windows fallback for the <c>tree</c> command. Builds <see cref="TreeEntryInfo"/>
/// objects directly from the filesystem without invoking an external binary.
/// </summary>
internal static class WindowsTreeTraversal
{
    internal sealed record Options(
        bool ShowHidden,
        bool DirectoriesOnly,
        bool FullPath,
        int? MaxDepth,
        string? IncludePattern,
        string? ExcludePattern,
        IReadOnlyList<string> Paths);

    /// <summary>Parses tree options from the remaining (non-selection) command arguments.</summary>
    internal static Options ParseOptions(IReadOnlyList<object?> arguments, string currentDirectory)
    {
        var showHidden = false;
        var directoriesOnly = false;
        var fullPath = false;
        int? maxDepth = null;
        string? includePattern = null;
        string? excludePattern = null;
        var paths = new List<string>();

        var serialized = arguments
            .Select(ExternalTextSerializer.SerializeArgument)
            .ToArray();

        for (var i = 0; i < serialized.Length; i++)
        {
            var arg = serialized[i];

            switch (arg)
            {
                case "-a":
                    showHidden = true;
                    break;
                case "-d":
                    directoriesOnly = true;
                    break;
                case "-f":
                    fullPath = true;
                    break;
                case "-L" or "--level":
                    if (i + 1 < serialized.Length && int.TryParse(serialized[i + 1], out var depth) && depth > 0)
                    {
                        maxDepth = depth;
                        i++;
                    }
                    break;
                case "-P" or "--pattern":
                    if (i + 1 < serialized.Length)
                    {
                        includePattern = serialized[++i];
                    }
                    break;
                case "-I" or "--exclude":
                    if (i + 1 < serialized.Length)
                    {
                        excludePattern = serialized[++i];
                    }
                    break;
                default:
                    if (!arg.StartsWith('-'))
                    {
                        // Positional argument: resolve relative to current directory
                        paths.Add(Path.IsPathRooted(arg)
                            ? arg
                            : Path.GetFullPath(arg, currentDirectory));
                    }
                    break;
            }
        }

        if (paths.Count == 0)
        {
            paths.Add(currentDirectory);
        }

        return new Options(showHidden, directoriesOnly, fullPath, maxDepth, includePattern, excludePattern, paths);
    }

    /// <summary>Builds a root <see cref="TreeEntryInfo"/> for <paramref name="rootPath"/>.</summary>
    internal static TreeEntryInfo BuildRoot(string rootPath, Options options)
    {
        var info = new DirectoryInfo(rootPath);
        var name = options.FullPath ? rootPath : info.Name;

        return new TreeEntryInfo
        {
            Name = name,
            Type = "dir",
            FullPath = rootPath,
            Modified = TryGetModified(info),
            Depth = 0,
            Children = BuildChildren(rootPath, options, depth: 1),
        };
    }

    private static IReadOnlyList<TreeEntryInfo> BuildChildren(string directoryPath, Options options, int depth)
    {
        if (options.MaxDepth.HasValue && depth > options.MaxDepth.Value)
        {
            return Array.Empty<TreeEntryInfo>();
        }

        IEnumerable<FileSystemInfo> entries;

        try
        {
            var dir = new DirectoryInfo(directoryPath);
            entries = dir.EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly)
                         .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return Array.Empty<TreeEntryInfo>();
        }

        var results = new List<TreeEntryInfo>();

        foreach (var entry in entries)
        {
            if (!options.ShowHidden && IsHidden(entry))
            {
                continue;
            }

            var isDir = entry is DirectoryInfo || entry.Attributes.HasFlag(FileAttributes.Directory);
            var isLink = entry.Attributes.HasFlag(FileAttributes.ReparsePoint);

            if (options.DirectoriesOnly && !isDir)
            {
                continue;
            }

            if (options.ExcludePattern is not null &&
                GlobPatternMatcher.IsMatch(entry.Name, options.ExcludePattern, ignoreCase: true))
            {
                continue;
            }

            if (options.IncludePattern is not null &&
                !GlobPatternMatcher.IsMatch(entry.Name, options.IncludePattern, ignoreCase: true) &&
                !isDir)
            {
                continue;
            }

            var fullPath = entry.FullName;
            var name = options.FullPath ? fullPath : entry.Name;
            var type = isLink ? "link" : isDir ? "dir" : "file";
            var linkTarget = isLink ? TryGetLinkTarget(entry) : null;

            StorageSize? size = null;
            if (!isDir && entry is FileInfo fileInfo)
            {
                try { size = StorageSize.FromBytes(fileInfo.Length); } catch { }
            }

            var children = isDir && !isLink
                ? BuildChildren(fullPath, options, depth + 1)
                : Array.Empty<TreeEntryInfo>();

            results.Add(new TreeEntryInfo
            {
                Name = name,
                Type = type,
                FullPath = fullPath,
                Size = size,
                Modified = TryGetModified(entry),
                LinkTarget = linkTarget,
                Depth = depth,
                Children = children,
            });
        }

        return results;
    }

    private static bool IsHidden(FileSystemInfo entry) =>
        entry.Name.StartsWith('.') ||
        entry.Attributes.HasFlag(FileAttributes.Hidden);

    private static DateTimeOffset? TryGetModified(FileSystemInfo entry)
    {
        try { return new DateTimeOffset(entry.LastWriteTimeUtc, TimeSpan.Zero); }
        catch { return null; }
    }

    private static string? TryGetLinkTarget(FileSystemInfo entry)
    {
        try { return entry.LinkTarget; }
        catch { return null; }
    }
}
