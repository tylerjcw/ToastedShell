using Tosh.Runtime;

namespace Tosh.Stdlib.Filesystem;

[CommandCategory("Filesystem")]
[CommandArgument("path ...", "Optional roots to measure.", Required = false, TypeName = "path-like")]
[CommandOption("-a", "Include file rows as well as directory summaries.")]
[CommandOption("-s", "Summarize each root instead of emitting recursive rows.")]
[CommandOption("-d <depth>", "Limit recursion depth.")]
[CommandOption("-h", "Accepts the familiar human-readable flag; ToSh sizes are already typed and human-friendly.")]
[CommandOption("-c", "Appends a typed grand total row.")]
[CommandOption("-x", "Stay on the same filesystem as each requested root.")]
[CommandOption("--time", "Include the latest modified timestamp for each emitted row.")]
[CommandOption("-t <size>", "Exclude entries smaller than size (or with - prefix, larger than absolute size).")]
[CommandOption("--threshold <size>", "Alias for -t.")]
[CommandOption("--exclude <pattern>", "Exclude files matching the shell glob pattern.")]
[CommandOption("--show <columns>", "Select which properties are rendered (display-only column selection).")]
[CommandOption("--hide <columns>", "Hide specific properties from the output.")]
[CommandOption("--show-all", "Display every available column.")]
[CommandExample("du -s .")]
[CommandExample("du -a -c --time")]
[CommandExample("du -x ./projects | get { Name, Size, Modified }")]
[CommandOutput("Produces typed path-usage objects with optional modified-time metadata and aggregate totals.")]
[PipelineInput(AcceptsList = true, Description = "Uses piped path-like roots when explicit paths are omitted. Falls back to the current directory when neither are present.")]
public sealed class DuCommand : ShellCommand
{
    public DuCommand(string name = "du")
        : base(name, "Returns disk usage information for files and directories.", $"{name} [-a] [-s] [-d depth] [-h] [-c] [-x] [--time] [-t size] [--exclude pattern] [--show columns] [--hide columns] [--show-all] [path ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var selection = CommandDisplaySelectionParser.Parse(context.Arguments);
        var options = ParseOptions(selection.RemainingArguments);
        var effectiveSelection = GetEffectiveSelection(selection.Selection, options);
        var pipedPaths = options.Paths.Count == 0
            ? await ShellPathArguments.CollectAsync(context, Array.Empty<object?>(), context.CancellationToken)
            : Array.Empty<string>();
        var paths = options.Paths.Count > 0
            ? ShellPathArguments.ExpandMany(context.Shell().CurrentDirectory, options.Paths)
            : pipedPaths.Count > 0
                ? pipedPaths
                : [context.Shell().CurrentDirectory];
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
                        context.Shell(),
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
                yield return CommandDisplaySelectionParser.Apply(context.Shell(), effectiveSelection, entry);
            }
        }

        if (options.IncludeTotal && paths.Count > 0)
        {
            yield return CommandDisplaySelectionParser.Apply(
                context.Shell(),
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

            if (options.ExcludePattern is not null &&
                GlobPatternMatcher.IsMatch(entry.Name, options.ExcludePattern, ignoreCase: true))
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
                    var fileSize = StorageSize.FromBytes(file.Length);

                    if (!ExcludedByThreshold(fileSize, options))
                    {
                        output.Add(new PathUsageInfo(
                            file.Name,
                            file.FullName,
                            depth + 1,
                            IsDirectory: false,
                            fileSize,
                            options.IncludeTime ? ToInstant(file.LastWriteTime) : null));
                    }
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
                    var childSize = StorageSize.FromBytes(childSummary.TotalBytes);

                    if (!ExcludedByThreshold(childSize, options))
                    {
                        output.Add(new PathUsageInfo(
                            childDirectory.Name,
                            childDirectory.FullName,
                            depth + 1,
                            IsDirectory: true,
                            childSize,
                            childSummary.Modified));
                    }
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

    private static bool ExcludedByThreshold(StorageSize size, DuOptions options)
    {
        if (options.ThresholdMin is not null && size.Bytes < options.ThresholdMin.Value.Bytes)
        {
            return true;
        }

        if (options.ThresholdMax is not null && size.Bytes > options.ThresholdMax.Value.Bytes)
        {
            return true;
        }

        return false;
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
            case "threshold":
                ParseThreshold(inlineValue ?? RequireOptionValue(arguments, ref index, "--threshold"), options);
                return;
            case "exclude":
                options.ExcludePattern = inlineValue ?? RequireOptionValue(arguments, ref index, "--exclude");
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
                case 't':
                    var thresholdValue = characterIndex + 1 < text.Length
                        ? text[(characterIndex + 1)..]
                        : RequireOptionValue(arguments, ref index, "-t");
                    ParseThreshold(thresholdValue, options);
                    return;
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

    private static void ParseThreshold(string spec, DuOptions options)
    {
        if (spec.StartsWith("-", StringComparison.Ordinal))
        {
            if (!StorageSize.TryParse(spec[1..], out var size))
            {
                throw new InvalidOperationException($"Cannot parse threshold '{spec}'. Use formats like '1M', '10K'.");
            }

            options.ThresholdMax = size;
        }
        else
        {
            if (!StorageSize.TryParse(spec, out var size))
            {
                throw new InvalidOperationException($"Cannot parse threshold '{spec}'. Use formats like '1M', '10K'.");
            }

            options.ThresholdMin = size;
        }
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

        public StorageSize? ThresholdMin { get; set; }

        public StorageSize? ThresholdMax { get; set; }

        public string? ExcludePattern { get; set; }
    }

    private sealed record DirectoryUsageResult(IReadOnlyList<PathUsageInfo> Entries, long TotalBytes);

    private sealed record DirectoryUsageSummary(long TotalBytes, DateTimeOffset? Modified);
}
