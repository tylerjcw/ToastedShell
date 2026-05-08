using Tosh.Runtime;

namespace Tosh.Stdlib.Filesystem;

[CommandCategory("Filesystem")]
[CommandArgument("path ...", "Optional paths used to resolve the containing mounted filesystem.", Required = false, TypeName = "path-like")]
[CommandOption("-h", "Accepts the familiar human-readable flag; ToSh sizes are already typed and human-friendly.")]
[CommandOption("-T", "Ensures the filesystem type column is visible.")]
[CommandOption("-l", "Restricts the output to local filesystems.")]
[CommandOption("-t <type[,type...]>", "Includes only matching filesystem types.")]
[CommandOption("-x <type[,type...]>", "Excludes matching filesystem types.")]
[CommandOption("--total", "Appends a typed aggregate total row.")]
[CommandOption("-i", "Displays inode usage instead of block usage.")]
[CommandOption("--output <columns>", "Selects which filesystem properties are rendered.")]
[CommandOption("--show <columns>", "Select which properties are rendered (display-only column selection).")]
[CommandOption("--hide <columns>", "Hide specific properties from the output.")]
[CommandOption("--show-all", "Display every available column.")]
[CommandExample("df --total")]
[CommandExample("df -t ext4 --show FileSystem,Type,UsePercent,MountedOn")]
[CommandExample("df . | get { FileSystem, MountedOn }")]
[CommandOutput("Produces typed filesystem usage objects, with optional aggregate totals.")]
[PipelineInput(AcceptsList = true, Description = "Uses piped path-like values when explicit paths are omitted. Without path input, `df` lists the full mounted filesystem set.")]
public sealed class DfCommand : ShellCommand
{
    public DfCommand(string name = "df")
        : base(name, "Returns mounted file system usage information.", $"{name} [-hTli] [-t type[,type...]] [-x type[,type...]] [--total] [--output columns] [--show columns] [--hide columns] [--show-all] [path ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var selection = CommandDisplaySelectionParser.Parse(context.Arguments, showOptionAliases: ["--output"]);
        var options = ParseOptions(selection.RemainingArguments);
        var effectiveSelection = GetEffectiveSelection(selection.Selection, options);
        var rawEntries = UnixSystemServices.GetFileSystemUsage()
            .Where(entry => MatchesFilters(entry, options))
            .ToArray();
        var entries = FileSystemUsageUtilities.GetDefaultVisibleEntries(rawEntries);
        var paths = await ShellPathArguments.CollectAsync(context, options.Paths, context.CancellationToken);
        var yielded = new List<FileSystemUsageInfo>();

        if (paths.Count == 0)
        {
            foreach (var entry in entries)
            {
                yielded.Add(entry);
                yield return CommandDisplaySelectionParser.Apply(context.Runtime, effectiveSelection, entry);
            }

            if (options.IncludeTotal && yielded.Count > 0)
            {
                yield return CommandDisplaySelectionParser.Apply(
                    context.Runtime,
                    effectiveSelection,
                    FileSystemUsageUtilities.CreateTotalRow(yielded));
            }

            yield break;
        }

        var yieldedMounts = new HashSet<string>(StringComparer.Ordinal);

        foreach (var resolvedPath in paths)
        {
            if (!File.Exists(resolvedPath) && !Directory.Exists(resolvedPath))
            {
                throw new InvalidOperationException($"Path '{resolvedPath}' does not exist.");
            }

            var match = FileSystemUsageUtilities.FindContainingMount(rawEntries, resolvedPath);

            if (match is not null && yieldedMounts.Add(match.MountedOn))
            {
                yielded.Add(match);
                yield return CommandDisplaySelectionParser.Apply(context.Runtime, effectiveSelection, match);
            }
        }

        if (options.IncludeTotal && yielded.Count > 0)
        {
            yield return CommandDisplaySelectionParser.Apply(
                context.Runtime,
                effectiveSelection,
                FileSystemUsageUtilities.CreateTotalRow(yielded));
        }
    }

    private static DisplayColumnSelection GetEffectiveSelection(DisplayColumnSelection selection, DfOptions options)
    {
        if (selection.HasOverrides)
        {
            return selection;
        }

        if (options.Inodes)
        {
            return new DisplayColumnSelection(showColumns: ["FileSystem", "InodesTotal", "InodesUsed", "InodesFree", "InodeUsePercent", "MountedOn"]);
        }

        if (options.PrintType)
        {
            return new DisplayColumnSelection(showColumns: ["FileSystem", "Type", "Size", "Used", "Available", "UsePercent", "MountedOn"]);
        }

        return selection;
    }

    private static bool MatchesFilters(FileSystemUsageInfo entry, DfOptions options)
    {
        if (options.LocalOnly && !entry.IsLocal)
        {
            return false;
        }

        if (options.IncludeTypes.Count > 0 &&
            (string.IsNullOrWhiteSpace(entry.Type) || !options.IncludeTypes.Contains(entry.Type)))
        {
            return false;
        }

        if (options.ExcludeTypes.Count > 0 &&
            !string.IsNullOrWhiteSpace(entry.Type) &&
            options.ExcludeTypes.Contains(entry.Type))
        {
            return false;
        }

        return true;
    }

    private static DfOptions ParseOptions(IReadOnlyList<object?> arguments)
    {
        var options = new DfOptions();
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

    private static void ParseLongOption(string text, IReadOnlyList<object?> arguments, ref int index, DfOptions options)
    {
        SplitLongOption(text, out var name, out var inlineValue);

        switch (name)
        {
            case "human-readable":
                return;
            case "print-type":
                options.PrintType = true;
                return;
            case "local":
                options.LocalOnly = true;
                return;
            case "total":
                options.IncludeTotal = true;
                return;
            case "inodes":
                options.Inodes = true;
                return;
            case "type":
                AddStrings(options.IncludeTypes, inlineValue ?? RequireOptionValue(arguments, ref index, "--type"));
                return;
            case "exclude-type":
                AddStrings(options.ExcludeTypes, inlineValue ?? RequireOptionValue(arguments, ref index, "--exclude-type"));
                return;
            default:
                throw new InvalidOperationException($"Unsupported df option '{text}'.");
        }
    }

    private static void ParseShortOptions(string text, IReadOnlyList<object?> arguments, ref int index, DfOptions options)
    {
        for (var characterIndex = 1; characterIndex < text.Length; characterIndex++)
        {
            var option = text[characterIndex];

            switch (option)
            {
                case 'h':
                    break;
                case 'T':
                    options.PrintType = true;
                    break;
                case 'l':
                    options.LocalOnly = true;
                    break;
                case 'i':
                    options.Inodes = true;
                    break;
                case 't':
                case 'x':
                    var value = characterIndex + 1 < text.Length
                        ? text[(characterIndex + 1)..]
                        : RequireOptionValue(arguments, ref index, $"-{option}");

                    if (option == 't')
                    {
                        AddStrings(options.IncludeTypes, value);
                    }
                    else
                    {
                        AddStrings(options.ExcludeTypes, value);
                    }

                    return;
                default:
                    throw new InvalidOperationException($"Unsupported df option '-{option}'.");
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

    private static void AddStrings(HashSet<string> target, string specification)
    {
        foreach (var candidate in specification.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                target.Add(candidate);
            }
        }
    }

    private sealed class DfOptions
    {
        public List<object?> Paths { get; } = [];

        public HashSet<string> IncludeTypes { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> ExcludeTypes { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool PrintType { get; set; }

        public bool LocalOnly { get; set; }

        public bool IncludeTotal { get; set; }

        public bool Inodes { get; set; }
    }
}
