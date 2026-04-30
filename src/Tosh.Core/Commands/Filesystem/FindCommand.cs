using System.Text.RegularExpressions;

namespace Tosh.Core.Commands.Filesystem;

[Stdlib(StdlibCategory.Filesystem)]
[CommandCategory("Filesystem")]
[CommandArgument("root ...", "One or more filesystem roots to search.", Required = false, TypeName = "path-like")]
[CommandOption("-name <pattern>", "Filter by shell-style name pattern.")]
[CommandOption("-iname <pattern>", "Case-insensitive name pattern.")]
[CommandOption("-path <pattern>", "Filter by shell-style path pattern against the root-relative path.")]
[CommandOption("-ipath <pattern>", "Case-insensitive path pattern.")]
[CommandOption("-regex <pattern>", "Filter by .NET regex against the root-relative path.")]
[CommandOption("-iregex <pattern>", "Case-insensitive regex filter against the root-relative path.")]
[CommandOption("-type <file|dir|link>", "Filter by filesystem entry kind.")]
[CommandOption("-perm <mode>", "Filter by Unix permission bits (octal). Prefix with - for 'at least' or / for 'any of'.")]
[CommandExample("find . -name *.tosh")]
[CommandExample("find . -path */src/*.cs", Title = "Match on relative path")]
[CommandExample("find . -perm -755", Title = "Files with at least rwxr-xr-x")]
[CommandExample("find . -regex \".*\\\\.tosh$\"")]
[CommandExample("find . -iregex \".*readme.*\"")]
[CommandOutput("Returns typed filesystem entries with rich metadata that flow naturally through the object pipeline.", TypeName = "FileSystemEntry", Members = "Name, FullName, Type, Size, Modified")]
[CommandSideEffects(ReadsFiles = true)]
[PipelineInput(AcceptsList = true, Description = "Uses piped path-like roots when explicit roots are omitted. Falls back to the current directory when neither are present.")]
public sealed class FindCommand : ShellCommand
{
    public FindCommand()
        : base("find", "Recursively finds file system entries.",
            "find [path ...] [-name pattern] [-iname pattern] [-path pattern] [-ipath pattern] [-regex pattern] [-iregex pattern] [-type f|d|l] [-perm mode] [-maxdepth n] [-mindepth n] [-size +/-size] [-mtime +/-days] [-newer-than duration] [-older-than duration] [-empty]")
    { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsedOptions = ParseOptions(context.Arguments, context.Runtime.CurrentDirectory);
        var options = parsedOptions.PathRegexPattern is null
            ? parsedOptions
            : parsedOptions with
            {
                PathRegex = ShellRegexUtilities.CompileRegex(
                    context,
                    parsedOptions.PathRegexPattern,
                    RegexOptions.Compiled | RegexOptions.CultureInvariant | (parsedOptions.PathRegexIgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None),
                    parsedOptions.PathRegexArgumentIndex,
                    TimeSpan.FromSeconds(5)),
            };
        var pipedRoots = options.RootArguments.Count == 0
            ? await ShellPathArguments.CollectAsync(context, Array.Empty<object?>(), context.CancellationToken)
            : Array.Empty<string>();
        var roots = options.RootArguments.Count > 0
            ? ShellPathArguments.ExpandMany(context.Runtime.CurrentDirectory, options.RootArguments)
            : pipedRoots.Count > 0
                ? pipedRoots
                : [context.Runtime.CurrentDirectory];

        foreach (var root in roots)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(root))
            {
                var file = new FileInfo(root);

                if (Matches(file, file.Name, 0, options))
                {
                    yield return FileSystemEntry.From(file);
                }

                continue;
            }

            if (!Directory.Exists(root))
            {
                throw new InvalidOperationException($"Path '{root}' does not exist.");
            }

            await foreach (var entry in EnumerateAsync(new DirectoryInfo(root), root, 0, options, context.CancellationToken))
            {
                yield return entry;
            }
        }
    }

    private static async IAsyncEnumerable<object?> EnumerateAsync(
        DirectoryInfo directory,
        string rootPath,
        int depth,
        FindOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var relativePath = depth == 0
            ? "."
            : NormalizeRelativePath(Path.GetRelativePath(rootPath, directory.FullName));

        if (Matches(directory, relativePath, depth, options))
        {
            yield return FileSystemEntry.From(directory);
        }

        if (depth >= options.MaxDepth || directory.LinkTarget is not null)
        {
            yield break;
        }

        IEnumerable<FileSystemInfo> entries;

        try
        {
            entries = directory.EnumerateFileSystemInfos();
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }
        catch (IOException)
        {
            yield break;
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry is DirectoryInfo childDirectory)
            {
                await foreach (var child in EnumerateAsync(childDirectory, rootPath, depth + 1, options, cancellationToken))
                {
                    yield return child;
                }
            }
            else if (Matches(entry, NormalizeRelativePath(Path.GetRelativePath(rootPath, entry.FullName)), depth + 1, options))
            {
                yield return FileSystemEntry.From(entry);
            }
        }
    }

    private static bool Matches(FileSystemInfo entry, string relativePath, int depth, FindOptions options)
    {
        if (depth < options.MinDepth)
        {
            return false;
        }

        if (options.NamePattern is not null &&
            !GlobPatternMatcher.IsMatch(entry.Name, options.NamePattern, options.NamePatternIgnoreCase))
        {
            return false;
        }

        if (options.PathPattern is not null &&
            !GlobPatternMatcher.IsMatch(relativePath, options.PathPattern, options.PathPatternIgnoreCase))
        {
            return false;
        }

        if (options.PathRegex is not null &&
            !options.PathRegex.IsMatch(relativePath))
        {
            return false;
        }

        if (options.TypeFilter is not null)
        {
            var matchesType = options.TypeFilter switch
            {
                'f' => entry is FileInfo,
                'd' => entry is DirectoryInfo,
                'l' => entry.LinkTarget is not null,
                _ => true,
            };

            if (!matchesType)
            {
                return false;
            }
        }

        if (options.Empty)
        {
            var isEmpty = entry switch
            {
                FileInfo file => file.Length == 0,
                DirectoryInfo dir => !dir.EnumerateFileSystemInfos().Any(),
                _ => false,
            };

            if (!isEmpty)
            {
                return false;
            }
        }

        if (options.MinSize is not null || options.MaxSize is not null)
        {
            if (entry is not FileInfo sizeFile)
            {
                return false;
            }

            if (options.MinSize is not null && sizeFile.Length < options.MinSize.Value.Bytes)
            {
                return false;
            }

            if (options.MaxSize is not null && sizeFile.Length > options.MaxSize.Value.Bytes)
            {
                return false;
            }
        }

        if (options.NewerThan is not null && entry.LastWriteTimeUtc < options.NewerThan.Value)
        {
            return false;
        }

        if (options.OlderThan is not null && entry.LastWriteTimeUtc > options.OlderThan.Value)
        {
            return false;
        }

        if (options.PermFilter is not null && OperatingSystem.IsLinux())
        {
            var mode = entry.UnixFileMode;
            var filter = options.PermFilter.Value;

            var matches = filter.Mode switch
            {
                PermMatchMode.Exact => ((int)mode & 0xFFF) == filter.Bits,
                PermMatchMode.AllOf => ((int)mode & filter.Bits) == filter.Bits,
                PermMatchMode.AnyOf => ((int)mode & filter.Bits) != 0,
                _ => true,
            };

            if (!matches)
            {
                return false;
            }
        }

        return true;
    }

    private static FindOptions ParseOptions(IReadOnlyList<object?> arguments, string currentDirectory)
    {
        var roots = new List<object?>();
        string? namePattern = null;
        var namePatternIgnoreCase = false;
        string? pathPattern = null;
        var pathPatternIgnoreCase = false;
        string? pathRegexPattern = null;
        var pathRegexIgnoreCase = false;
        var pathRegexArgumentIndex = -1;
        char? typeFilter = null;
        var maxDepth = int.MaxValue;
        var minDepth = 0;
        var empty = false;
        StorageSize? minSize = null;
        StorageSize? maxSize = null;
        DateTime? newerThan = null;
        DateTime? olderThan = null;
        PermFilter? permFilter = null;
        var parsingRoots = true;

        for (var index = 0; index < arguments.Count; index++)
        {
            var text = arguments[index]?.ToString();

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (parsingRoots && !text.StartsWith("-", StringComparison.Ordinal))
            {
                roots.Add(arguments[index]);
                continue;
            }

            parsingRoots = false;

            switch (text)
            {
                case "-name":
                    namePattern = CommandArguments.RequireString(arguments, ++index, "pattern");
                    namePatternIgnoreCase = false;
                    break;
                case "-iname":
                    namePattern = CommandArguments.RequireString(arguments, ++index, "pattern");
                    namePatternIgnoreCase = true;
                    break;
                case "-path":
                    pathPattern = CommandArguments.RequireString(arguments, ++index, "pattern");
                    pathPatternIgnoreCase = false;
                    break;
                case "-ipath":
                    pathPattern = CommandArguments.RequireString(arguments, ++index, "pattern");
                    pathPatternIgnoreCase = true;
                    break;
                case "-regex":
                    pathRegexPattern = CommandArguments.RequireString(arguments, ++index, "pattern");
                    pathRegexIgnoreCase = false;
                    pathRegexArgumentIndex = index;
                    break;
                case "-iregex":
                    pathRegexPattern = CommandArguments.RequireString(arguments, ++index, "pattern");
                    pathRegexIgnoreCase = true;
                    pathRegexArgumentIndex = index;
                    break;
                case "-type":
                    typeFilter = CommandArguments.RequireString(arguments, ++index, "type") switch
                    {
                        "f" => 'f',
                        "d" => 'd',
                        "l" => 'l',
                        var value => throw new InvalidOperationException($"Unsupported find type '{value}'."),
                    };
                    break;
                case "-maxdepth":
                    maxDepth = CommandArguments.RequireConverted<int>(arguments, ++index, "maxdepth");
                    break;
                case "-mindepth":
                    minDepth = CommandArguments.RequireConverted<int>(arguments, ++index, "mindepth");
                    break;
                case "-empty":
                    empty = true;
                    break;
                case "-size":
                    var sizeSpec = CommandArguments.RequireString(arguments, ++index, "size");
                    (minSize, maxSize) = ParseSizeSpec(sizeSpec);
                    break;
                case "-mtime":
                    var mtimeSpec = CommandArguments.RequireString(arguments, ++index, "days");
                    (newerThan, olderThan) = ParseMtimeSpec(mtimeSpec);
                    break;
                case "-newer-than":
                    var newerSpec = CommandArguments.RequireString(arguments, ++index, "duration");

                    if (!TemporalParser.TryParseDuration(newerSpec, out var newerDuration))
                    {
                        throw new InvalidOperationException($"Cannot parse duration '{newerSpec}'. Use formats like '7d', '2h', '30m'.");
                    }

                    newerThan = DateTime.UtcNow - newerDuration;
                    break;
                case "-older-than":
                    var olderSpec = CommandArguments.RequireString(arguments, ++index, "duration");

                    if (!TemporalParser.TryParseDuration(olderSpec, out var olderDuration))
                    {
                        throw new InvalidOperationException($"Cannot parse duration '{olderSpec}'. Use formats like '7d', '2h', '30m'.");
                    }

                    olderThan = DateTime.UtcNow - olderDuration;
                    break;
                case "-perm":
                    var permSpec = CommandArguments.RequireString(arguments, ++index, "mode");
                    permFilter = ParsePermSpec(permSpec);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported find option '{text}'.");
            }
        }

        return new FindOptions(roots, namePattern, namePatternIgnoreCase, pathPattern, pathPatternIgnoreCase, pathRegexPattern, pathRegexIgnoreCase, pathRegexArgumentIndex, typeFilter, minDepth, maxDepth, null, empty, minSize, maxSize, newerThan, olderThan, permFilter);
    }

    private static (StorageSize? Min, StorageSize? Max) ParseSizeSpec(string spec)
    {
        if (spec.StartsWith("+", StringComparison.Ordinal))
        {
            if (!StorageSize.TryParse(spec[1..], out var size))
            {
                throw new InvalidOperationException($"Cannot parse size '{spec[1..]}'. Use formats like '1K', '10M', '1G'.");
            }

            return (size, null);
        }

        if (spec.StartsWith("-", StringComparison.Ordinal))
        {
            if (!StorageSize.TryParse(spec[1..], out var size))
            {
                throw new InvalidOperationException($"Cannot parse size '{spec[1..]}'. Use formats like '1K', '10M', '1G'.");
            }

            return (null, size);
        }

        if (!StorageSize.TryParse(spec, out var exactSize))
        {
            throw new InvalidOperationException($"Cannot parse size '{spec}'. Use formats like '1K', '10M', '1G'.");
        }

        return (exactSize, exactSize);
    }

    private static (DateTime? NewerThan, DateTime? OlderThan) ParseMtimeSpec(string spec)
    {
        if (spec.StartsWith("+", StringComparison.Ordinal))
        {
            if (!int.TryParse(spec[1..], out var days))
            {
                throw new InvalidOperationException($"Cannot parse mtime '{spec}'. Use +N or -N where N is days.");
            }

            return (null, DateTime.UtcNow.AddDays(-days));
        }

        if (spec.StartsWith("-", StringComparison.Ordinal))
        {
            if (!int.TryParse(spec[1..], out var days))
            {
                throw new InvalidOperationException($"Cannot parse mtime '{spec}'. Use +N or -N where N is days.");
            }

            return (DateTime.UtcNow.AddDays(-days), null);
        }

        if (!int.TryParse(spec, out var exactDays))
        {
            throw new InvalidOperationException($"Cannot parse mtime '{spec}'. Use +N or -N where N is days.");
        }

        var start = DateTime.UtcNow.AddDays(-(exactDays + 1));
        var end = DateTime.UtcNow.AddDays(-exactDays);
        return (start, end);
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static PermFilter ParsePermSpec(string spec)
    {
        PermMatchMode mode;
        string digits;

        if (spec.StartsWith("-", StringComparison.Ordinal))
        {
            mode = PermMatchMode.AllOf;
            digits = spec[1..];
        }
        else if (spec.StartsWith("/", StringComparison.Ordinal))
        {
            mode = PermMatchMode.AnyOf;
            digits = spec[1..];
        }
        else
        {
            mode = PermMatchMode.Exact;
            digits = spec;
        }

        if (!TryParseOctal(digits, out var bits))
        {
            throw new InvalidOperationException($"Cannot parse permission '{spec}'. Use octal like 755, -644, /222.");
        }

        return new PermFilter(mode, bits);
    }

    private static bool TryParseOctal(string text, out int value)
    {
        value = 0;

        foreach (var ch in text)
        {
            if (ch is < '0' or > '7')
            {
                return false;
            }

            value = (value << 3) | (ch - '0');
        }

        return text.Length > 0;
    }

    private sealed record FindOptions(
        IReadOnlyList<object?> RootArguments,
        string? NamePattern,
        bool NamePatternIgnoreCase,
        string? PathPattern,
        bool PathPatternIgnoreCase,
        string? PathRegexPattern,
        bool PathRegexIgnoreCase,
        int PathRegexArgumentIndex,
        char? TypeFilter,
        int MinDepth,
        int MaxDepth,
        Regex? PathRegex,
        bool Empty,
        StorageSize? MinSize,
        StorageSize? MaxSize,
        DateTime? NewerThan,
        DateTime? OlderThan,
        PermFilter? PermFilter);

    private readonly record struct PermFilter(PermMatchMode Mode, int Bits);

    private enum PermMatchMode
    {
        Exact,
        AllOf,
        AnyOf,
    }
}
