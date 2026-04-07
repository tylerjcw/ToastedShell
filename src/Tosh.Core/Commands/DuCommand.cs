namespace Tosh.Core.Commands;

[CommandCategory("Filesystem")]
public sealed class DuCommand : ShellCommand
{
    public DuCommand(string name = "du")
        : base(name, "Returns disk usage information for files and directories.", $"{name} [-a] [-s] [-d depth] [-h] [-c] [-x] [--time] [--show columns] [--hide columns] [--show-all] [path ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var selection = CommandDisplaySelectionParser.Parse(context.Arguments);
        var options = ParseOptions(selection.RemainingArguments);
        var effectiveSelection = GetEffectiveSelection(selection.Selection, options);
        var pipedPaths = options.Paths.Count == 0
            ? await ShellPathArguments.CollectAsync(context, Array.Empty<object?>(), context.CancellationToken)
            : Array.Empty<string>();
        var paths = options.Paths.Count > 0
            ? ShellPathArguments.ExpandMany(context.Runtime.CurrentDirectory, options.Paths)
            : pipedPaths.Count > 0
                ? pipedPaths
                : [context.Runtime.CurrentDirectory];
        var mountEntries = options.OneFileSystem ? UnixSystemServices.GetFileSystemUsage() : Array.Empty<FileSystemUsageInfo>();
        long totalBytes = 0;

        foreach (var path in paths)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(path))
            {
                var file = new FileInfo(path);

                if (options.IncludeFiles || options.Summarize)
                {
                    var size = StorageSize.FromBytes(file.Length);
                    totalBytes += file.Length;
                    yield return CommandDisplaySelectionParser.Apply(
                        context.Runtime,
                        effectiveSelection,
                        new PathUsageInfo(
                            file.Name,
                            file.FullName,
                            0,
                            IsDirectory: false,
                            size,
                            options.IncludeTime ? ToInstant(file.LastWriteTime) : null));
                }

                continue;
            }

            if (!Directory.Exists(path))
            {
                throw new InvalidOperationException($"Path '{path}' does not exist.");
            }

            var rootDirectory = new DirectoryInfo(path);
            var rootMountPoint = options.OneFileSystem
                ? FileSystemUsageUtilities.FindContainingMount(mountEntries, rootDirectory.FullName)?.MountedOn
                : null;
            var usage = GetDirectoryUsage(rootDirectory, options, rootMountPoint, mountEntries);
            totalBytes += usage.TotalBytes;

            foreach (var entry in usage.Entries)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                yield return CommandDisplaySelectionParser.Apply(context.Runtime, effectiveSelection, entry);
            }
        }

        if (options.IncludeTotal && paths.Count > 0)
        {
            yield return CommandDisplaySelectionParser.Apply(
                context.Runtime,
                effectiveSelection,
                new PathUsageInfo(
                    "total",
                    "total",
                    0,
                    IsDirectory: true,
                    StorageSize.FromBytes(totalBytes),
                    options.IncludeTime ? null : null,
                    IsTotal: true));
        }
    }

    private static DisplayColumnSelection GetEffectiveSelection(DisplayColumnSelection selection, DuOptions options)
    {
        if (selection.HasOverrides || !options.IncludeTime)
        {
            return selection;
        }

        return new DisplayColumnSelection(showColumns: ["Name", "Type", "Size", "Depth", "Modified"]);
    }

    private static DirectoryUsageResult GetDirectoryUsage(
        DirectoryInfo directory,
        DuOptions options,
        string? rootMountPoint,
        IReadOnlyList<FileSystemUsageInfo> mountEntries)
    {
        var output = new List<PathUsageInfo>();
        var summary = GetDirectoryUsageRecursive(directory, 0, options, output, rootMountPoint, mountEntries);

        if (options.Summarize)
        {
            return new DirectoryUsageResult(
                [
                    new PathUsageInfo(
                        directory.Name,
                        directory.FullName,
                        0,
                        IsDirectory: true,
                        StorageSize.FromBytes(summary.TotalBytes),
                        summary.Modified)
                ],
                summary.TotalBytes);
        }

        return new DirectoryUsageResult(output, summary.TotalBytes);
    }

    private static DirectoryUsageSummary GetDirectoryUsageRecursive(
        DirectoryInfo directory,
        int depth,
        DuOptions options,
        List<PathUsageInfo> output,
        string? rootMountPoint,
        IReadOnlyList<FileSystemUsageInfo> mountEntries)
    {
        long totalBytes = 0;
        var latestModified = options.IncludeTime ? ToInstant(directory.LastWriteTime) : (DateTimeOffset?)null;

        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null)
            {
                continue;
            }

            if (options.OneFileSystem &&
                rootMountPoint is not null &&
                FileSystemUsageUtilities.FindContainingMount(mountEntries, entry.FullName)?.MountedOn is { } entryMount &&
                !string.Equals(entryMount, rootMountPoint, StringComparison.Ordinal))
            {
                continue;
            }

            if (entry is FileInfo file)
            {
                totalBytes += file.Length;

                if (options.IncludeTime)
                {
                    latestModified = Max(latestModified, ToInstant(file.LastWriteTime));
                }

                if (options.IncludeFiles && depth + 1 <= options.MaxDepth)
                {
                    output.Add(new PathUsageInfo(
                        file.Name,
                        file.FullName,
                        depth + 1,
                        IsDirectory: false,
                        StorageSize.FromBytes(file.Length),
                        options.IncludeTime ? ToInstant(file.LastWriteTime) : null));
                }

                continue;
            }

            if (entry is DirectoryInfo childDirectory)
            {
                var childSummary = GetDirectoryUsageRecursive(childDirectory, depth + 1, options, output, rootMountPoint, mountEntries);
                totalBytes += childSummary.TotalBytes;

                if (options.IncludeTime)
                {
                    latestModified = Max(latestModified, childSummary.Modified);
                }

                if (depth + 1 <= options.MaxDepth)
                {
                    output.Add(new PathUsageInfo(
                        childDirectory.Name,
                        childDirectory.FullName,
                        depth + 1,
                        IsDirectory: true,
                        StorageSize.FromBytes(childSummary.TotalBytes),
                        childSummary.Modified));
                }
            }
        }

        if (depth <= options.MaxDepth)
        {
            output.Add(new PathUsageInfo(
                directory.Name,
                directory.FullName,
                depth,
                IsDirectory: true,
                StorageSize.FromBytes(totalBytes),
                latestModified));
        }

        return new DirectoryUsageSummary(totalBytes, latestModified);
    }

    private static DateTimeOffset? Max(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (left is null)
        {
            return right;
        }

        if (right is null)
        {
            return left;
        }

        return left.Value >= right.Value ? left : right;
    }

    private static DateTimeOffset ToInstant(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => new DateTimeOffset(value, TimeSpan.Zero),
            DateTimeKind.Local => new DateTimeOffset(value),
            _ => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Local)),
        };
    }

    private static DuOptions ParseOptions(IReadOnlyList<object?> arguments)
    {
        var options = new DuOptions();
        var parseOptions = true;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];

            if (!parseOptions || argument is not string text || text.Length == 0)
            {
                options.Paths.Add(argument);
                continue;
            }

            if (text == "--")
            {
                parseOptions = false;
                continue;
            }

            if (text.StartsWith("--", StringComparison.Ordinal))
            {
                ParseLongOption(text, arguments, ref index, options);
                continue;
            }

            if (text.StartsWith("-", StringComparison.Ordinal) && text.Length > 1)
            {
                ParseShortOptions(text, arguments, ref index, options);
                continue;
            }

            options.Paths.Add(argument);
        }

        return options;
    }

    private static void ParseLongOption(string text, IReadOnlyList<object?> arguments, ref int index, DuOptions options)
    {
        SplitLongOption(text, out var name, out var inlineValue);

        switch (name)
        {
            case "all":
                options.IncludeFiles = true;
                return;
            case "summarize":
                options.Summarize = true;
                options.MaxDepth = 0;
                return;
            case "max-depth":
                options.MaxDepth = ParseDepth(inlineValue ?? RequireOptionValue(arguments, ref index, "--max-depth"));
                return;
            case "human-readable":
                return;
            case "total":
                options.IncludeTotal = true;
                return;
            case "one-file-system":
                options.OneFileSystem = true;
                return;
            case "time":
                options.IncludeTime = true;
                return;
            default:
                throw new InvalidOperationException($"Unsupported du option '{text}'.");
        }
    }

    private static void ParseShortOptions(string text, IReadOnlyList<object?> arguments, ref int index, DuOptions options)
    {
        for (var characterIndex = 1; characterIndex < text.Length; characterIndex++)
        {
            var option = text[characterIndex];

            switch (option)
            {
                case 'a':
                    options.IncludeFiles = true;
                    break;
                case 's':
                    options.Summarize = true;
                    options.MaxDepth = 0;
                    break;
                case 'h':
                    break;
                case 'c':
                    options.IncludeTotal = true;
                    break;
                case 'x':
                    options.OneFileSystem = true;
                    break;
                case 'd':
                    var value = characterIndex + 1 < text.Length
                        ? text[(characterIndex + 1)..]
                        : RequireOptionValue(arguments, ref index, "-d");
                    options.MaxDepth = ParseDepth(value);
                    return;
                default:
                    throw new InvalidOperationException($"Unsupported du option '-{option}'.");
            }
        }
    }

    private static void SplitLongOption(string text, out string name, out string? value)
    {
        var separatorIndex = text.IndexOf('=', StringComparison.Ordinal);

        if (separatorIndex < 0)
        {
            name = text[2..];
            value = null;
            return;
        }

        name = text[2..separatorIndex];
        value = text[(separatorIndex + 1)..];
    }

    private static string RequireOptionValue(IReadOnlyList<object?> arguments, ref int index, string optionName)
    {
        index++;

        if (index >= arguments.Count || arguments[index]?.ToString() is not { Length: > 0 } text)
        {
            throw new InvalidOperationException($"Option '{optionName}' requires a value.");
        }

        return text;
    }

    private static int ParseDepth(string text)
    {
        if (!int.TryParse(text, out var depth) || depth < 0)
        {
            throw new InvalidOperationException($"Invalid du depth '{text}'.");
        }

        return depth;
    }

    private sealed class DuOptions
    {
        public List<object?> Paths { get; } = [];

        public bool IncludeFiles { get; set; }

        public bool Summarize { get; set; }

        public int MaxDepth { get; set; } = int.MaxValue;

        public bool IncludeTotal { get; set; }

        public bool OneFileSystem { get; set; }

        public bool IncludeTime { get; set; }
    }

    private sealed record DirectoryUsageResult(IReadOnlyList<PathUsageInfo> Entries, long TotalBytes);

    private sealed record DirectoryUsageSummary(long TotalBytes, DateTimeOffset? Modified);
}
