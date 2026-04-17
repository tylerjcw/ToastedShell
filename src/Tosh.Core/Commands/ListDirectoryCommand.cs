namespace Tosh.Core.Commands;

[CommandCategory("Filesystem")]
[CommandArgument("path ...", "Optional directories or files to list.", Required = false, TypeName = "path-like")]
[CommandOption("-a", "Include hidden entries.")]
[CommandOption("-A", "Include hidden entries while matching standard almost-all ls behavior.")]
[CommandOption("-l", "Use the long listing view.")]
[CommandOption("-d", "List directory arguments themselves instead of their contents.")]
[CommandOption("-R", "Traverse directories recursively.")]
[CommandOption("-F", "Classify names with shell-style suffixes like `*` and `@`.")]
[CommandOption("-i", "Include inode metadata in the compact table view.")]
[CommandOption("-r", "Reverse the current sort order.")]
[CommandOption("-S", "Sort by size descending.")]
[CommandOption("-t", "Sort by the active time field descending.")]
[CommandOption("--sort <name|size|time>", "Choose the primary listing sort field.")]
[CommandOption("--time <modified|access|created>", "Choose which time field long listings and time sorts use.")]
[CommandOption("--group-directories-first", "Group directories ahead of files before applying the primary sort.")]
[CommandOption("-la", "Combine hidden and long listing output.")]
[CommandExample("ls -la")]
[CommandExample("ls -R --group-directories-first")]
[CommandExample("ls -l --time access | where _.Type == file | get { Name, Accessed }")]
[CommandNote("Filesystem metadata stays typed in the pipeline, even when Tosh renders it like a shell table.")]
[CommandOutput("Produces typed filesystem entries that the display layer renders as shell tables by default.", TypeName = "FileSystemEntry", Members = "Name, Type, Size, Modified, Permissions, Owner, Group")]
[CommandSideEffects(ReadsFiles = true)]
[PipelineInput(Description = "Ls is still explicit-arg-first; path input is not yet consumed from the pipeline.")]
public sealed class ListDirectoryCommand : ShellCommand
{
    public ListDirectoryCommand()
        : base("ls", "Lists file system entries.", "ls [-aAldRFihrSt] [-T [depth]] [--sort name|size|time] [--time modified|access|created] [--group-directories-first] [--tree [depth]] [--show columns] [--hide columns] [--show-all] [path ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var selection = CommandDisplaySelectionParser.Parse(context.Arguments);
        var options = ParseOptions(selection.RemainingArguments);
        var paths = options.Paths.Count == 0
            ? [context.Runtime.CurrentDirectory]
            : ShellPathArguments.ExpandMany(context.Runtime.CurrentDirectory, options.Paths);

        foreach (var path in paths)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (Directory.Exists(path))
            {
                var directory = new DirectoryInfo(path);

                if (options.DirectorySelf)
                {
                    yield return CommandDisplaySelectionParser.Apply(
                        context.Runtime,
                        selection.Selection,
                        CreateEntry(directory, options));
                    continue;
                }

                if (options.TreeDepth is not null)
                {
                    foreach (var entry in BuildTreeEntries(directory, options, 0, context.CancellationToken))
                    {
                        context.CancellationToken.ThrowIfCancellationRequested();
                        yield return CommandDisplaySelectionParser.Apply(
                            context.Runtime,
                            selection.Selection,
                            entry);
                    }

                    continue;
                }

                foreach (var entry in EnumerateDirectoryEntries(directory, options, context.CancellationToken))
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    yield return CommandDisplaySelectionParser.Apply(
                        context.Runtime,
                        selection.Selection,
                        CreateEntry(entry, options));
                }

                continue;
            }

            if (File.Exists(path))
            {
                yield return CommandDisplaySelectionParser.Apply(
                    context.Runtime,
                    selection.Selection,
                    CreateEntry(new FileInfo(path), options));
                continue;
            }

            throw new InvalidOperationException($"Path '{path}' does not exist.");
        }
    }

    private static FileSystemEntry CreateEntry(FileSystemInfo entry, LsOptions options)
    {
        return FileSystemEntry.From(
            entry,
            options.PreferLongDisplay,
            options.ClassifyNames,
            options.IncludeInodeInShortDisplay,
            options.TimeField);
    }

    private static IReadOnlyList<FileSystemEntry> BuildTreeEntries(
        DirectoryInfo directory,
        LsOptions options,
        int currentDepth,
        CancellationToken cancellationToken)
    {
        var maxDepth = options.TreeDepth!.Value;
        var entries = GetSortedEntries(directory, options);
        var result = new List<FileSystemEntry>();

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry is DirectoryInfo childDirectory && !IsSymbolicLink(entry)
                && (maxDepth <= 0 || currentDepth + 1 < maxDepth))
            {
                var children = BuildTreeEntries(childDirectory, options, currentDepth + 1, cancellationToken);
                result.Add(CreateEntry(entry, options) with { Children = children });
            }
            else
            {
                result.Add(CreateEntry(entry, options));
            }
        }

        return result;
    }

    private static IEnumerable<FileSystemInfo> EnumerateDirectoryEntries(
        DirectoryInfo directory,
        LsOptions options,
        CancellationToken cancellationToken)
    {
        foreach (var entry in GetSortedEntries(directory, options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return entry;

            if (!options.Recursive || entry is not DirectoryInfo childDirectory || IsSymbolicLink(entry))
            {
                continue;
            }

            foreach (var descendant in EnumerateDirectoryEntries(childDirectory, options, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return descendant;
            }
        }
    }

    private static IReadOnlyList<FileSystemInfo> GetSortedEntries(DirectoryInfo directory, LsOptions options)
    {
        IEnumerable<FileSystemInfo> rawEntries;

        try
        {
            rawEntries = directory.EnumerateFileSystemInfos();
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Permission denied: '{directory.FullName}'.");
        }

        var entries = rawEntries
            .Where(entry => options.ShowHidden || !IsHiddenEntry(entry))
            .ToList();

        entries.Sort((left, right) => CompareEntries(left, right, options));
        return entries;
    }

    private static int CompareEntries(FileSystemInfo left, FileSystemInfo right, LsOptions options)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        var comparison = 0;

        if (options.GroupDirectoriesFirst)
        {
            comparison = CompareDirectoriesFirst(left, right);
        }

        if (comparison == 0)
        {
            comparison = options.SortMode switch
            {
                LsSortMode.Size => CompareDescending(GetSizeSortValue(left), GetSizeSortValue(right)),
                LsSortMode.Time => CompareDescending(GetTimeSortValue(left, options.TimeField), GetTimeSortValue(right, options.TimeField)),
                _ => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name),
            };
        }

        if (comparison == 0)
        {
            comparison = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
        }

        if (comparison == 0)
        {
            comparison = StringComparer.Ordinal.Compare(left.FullName, right.FullName);
        }

        return options.Reverse ? -comparison : comparison;
    }

    private static int CompareDirectoriesFirst(FileSystemInfo left, FileSystemInfo right)
    {
        var leftIsDirectory = left is DirectoryInfo;
        var rightIsDirectory = right is DirectoryInfo;

        if (leftIsDirectory == rightIsDirectory)
        {
            return 0;
        }

        return leftIsDirectory ? -1 : 1;
    }

    private static long GetSizeSortValue(FileSystemInfo entry)
    {
        return entry is FileInfo file ? file.Length : 0L;
    }

    private static DateTime GetTimeSortValue(FileSystemInfo entry, FileSystemEntryTimeField timeField)
    {
        return timeField switch
        {
            FileSystemEntryTimeField.Accessed => entry.LastAccessTimeUtc,
            FileSystemEntryTimeField.Created => entry.CreationTimeUtc,
            _ => entry.LastWriteTimeUtc,
        };
    }

    private static int CompareDescending<T>(T left, T right)
        where T : IComparable<T>
    {
        return right.CompareTo(left);
    }

    private static bool IsHiddenEntry(FileSystemInfo entry)
    {
        return entry.Name.StartsWith(".", StringComparison.Ordinal) || entry.Attributes.HasFlag(FileAttributes.Hidden);
    }

    private static bool IsSymbolicLink(FileSystemInfo entry)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(entry.LinkTarget);
        }
        catch
        {
            return false;
        }
    }

    private static LsOptions ParseOptions(IReadOnlyList<object?> arguments)
    {
        var paths = new List<object?>();
        var parseOptions = true;
        var options = new LsOptions();

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];

            if (!parseOptions || argument is not string text || text.Length == 0)
            {
                paths.Add(argument);
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

            paths.Add(argument);
        }

        return options with { Paths = paths };
    }

    private static void ParseLongOption(string text, IReadOnlyList<object?> arguments, ref int index, LsOptions options)
    {
        SplitLongOption(text, out var name, out var inlineValue);

        switch (name)
        {
            case "all":
            case "almost-all":
                options.ShowHidden = true;
                return;
            case "long":
                options.PreferLongDisplay = true;
                return;
            case "directory":
                options.DirectorySelf = true;
                return;
            case "recursive":
                options.Recursive = true;
                return;
            case "classify":
                options.ClassifyNames = true;
                return;
            case "inode":
                options.IncludeInodeInShortDisplay = true;
                return;
            case "human-readable":
                return;
            case "reverse":
                options.Reverse = true;
                return;
            case "group-directories-first":
                options.GroupDirectoriesFirst = true;
                return;
            case "tree":
                if (inlineValue is not null && int.TryParse(inlineValue, out var treeDepth))
                    options.TreeDepth = treeDepth;
                else
                    options.TreeDepth = 0;
                return;
            case "sort":
                options.SortMode = ParseSortMode(inlineValue ?? RequireOptionValue(arguments, ref index, "--sort"));
                return;
            case "time":
                options.TimeField = ParseTimeField(inlineValue ?? RequireOptionValue(arguments, ref index, "--time"));
                return;
            default:
                throw new InvalidOperationException($"Unknown option '{text}'.");
        }
    }

    private static void ParseShortOptions(string text, IReadOnlyList<object?> arguments, ref int index, LsOptions options)
    {
        foreach (var option in text[1..])
        {
            switch (option)
            {
                case 'a':
                case 'A':
                    options.ShowHidden = true;
                    break;
                case 'l':
                    options.PreferLongDisplay = true;
                    break;
                case 'd':
                    options.DirectorySelf = true;
                    break;
                case 'R':
                    options.Recursive = true;
                    break;
                case 'F':
                    options.ClassifyNames = true;
                    break;
                case 'i':
                    options.IncludeInodeInShortDisplay = true;
                    break;
                case 'h':
                    break;
                case 'r':
                    options.Reverse = true;
                    break;
                case 'S':
                    options.SortMode = LsSortMode.Size;
                    break;
                case 't':
                    options.SortMode = LsSortMode.Time;
                    break;
                case 'T':
                    options.TreeDepth = TryConsumeIntArgument(arguments, ref index) ?? 0;
                    break;
                default:
                    throw new InvalidOperationException($"Unknown option '-{option}'.");
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

    private static int? TryConsumeIntArgument(IReadOnlyList<object?> arguments, ref int index)
    {
        if (index + 1 < arguments.Count && arguments[index + 1]?.ToString() is { } next && int.TryParse(next, out var value))
        {
            index++;
            return value;
        }

        return null;
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

    private static LsSortMode ParseSortMode(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "name" => LsSortMode.Name,
            "size" => LsSortMode.Size,
            "time" => LsSortMode.Time,
            _ => throw new InvalidOperationException($"Unsupported ls sort mode '{value}'. Expected 'name', 'size', or 'time'."),
        };
    }

    private static FileSystemEntryTimeField ParseTimeField(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "modified" or "mtime" => FileSystemEntryTimeField.Modified,
            "access" or "accessed" or "atime" or "use" => FileSystemEntryTimeField.Accessed,
            "created" or "creation" or "birth" => FileSystemEntryTimeField.Created,
            _ => throw new InvalidOperationException($"Unsupported ls time field '{value}'. Expected 'modified', 'access', or 'created'."),
        };
    }

    private sealed record LsOptions
    {
        public bool ShowHidden { get; set; }

        public bool PreferLongDisplay { get; set; }

        public bool DirectorySelf { get; set; }

        public bool Recursive { get; set; }

        public bool ClassifyNames { get; set; }

        public bool IncludeInodeInShortDisplay { get; set; }

        public bool Reverse { get; set; }

        public bool GroupDirectoriesFirst { get; set; }

        public int? TreeDepth { get; set; }

        public LsSortMode SortMode { get; set; } = LsSortMode.Name;

        public FileSystemEntryTimeField TimeField { get; set; } = FileSystemEntryTimeField.Modified;

        public IReadOnlyList<object?> Paths { get; init; } = Array.Empty<object?>();
    }

    private enum LsSortMode
    {
        Name,
        Size,
        Time,
    }
}
