namespace Tosh.Core.Commands;

public sealed class FindCommand : ShellCommand
{
    public FindCommand()
        : base("find", "Recursively finds file system entries.", "find [path ...] [-name pattern] [-type f|d|l] [-maxdepth n] [-mindepth n]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var options = ParseOptions(context.Arguments, context.Runtime.CurrentDirectory);
        var roots = options.Roots.Count == 0 ? [context.Runtime.CurrentDirectory] : options.Roots;

        foreach (var root in roots)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(root))
            {
                var file = new FileInfo(root);

                if (Matches(file, 0, options))
                {
                    yield return FileSystemEntry.From(file);
                }

                continue;
            }

            if (!Directory.Exists(root))
            {
                throw new InvalidOperationException($"Path '{root}' does not exist.");
            }

            await foreach (var entry in EnumerateAsync(new DirectoryInfo(root), 0, options, context.CancellationToken))
            {
                yield return entry;
            }
        }
    }

    private static async IAsyncEnumerable<object?> EnumerateAsync(
        DirectoryInfo directory,
        int depth,
        FindOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Matches(directory, depth, options))
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
                await foreach (var child in EnumerateAsync(childDirectory, depth + 1, options, cancellationToken))
                {
                    yield return child;
                }
            }
            else if (Matches(entry, depth + 1, options))
            {
                yield return FileSystemEntry.From(entry);
            }
        }
    }

    private static bool Matches(FileSystemInfo entry, int depth, FindOptions options)
    {
        if (depth < options.MinDepth)
        {
            return false;
        }

        if (options.NamePattern is not null &&
            !GlobPatternMatcher.IsMatch(entry.Name, options.NamePattern))
        {
            return false;
        }

        return options.TypeFilter switch
        {
            null => true,
            'f' => entry is FileInfo,
            'd' => entry is DirectoryInfo,
            'l' => entry.LinkTarget is not null,
            _ => true,
        };
    }

    private static FindOptions ParseOptions(IReadOnlyList<object?> arguments, string currentDirectory)
    {
        var roots = new List<string>();
        string? namePattern = null;
        char? typeFilter = null;
        var maxDepth = int.MaxValue;
        var minDepth = 0;
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
                roots.Add(PathUtilities.ResolvePath(currentDirectory, text));
                continue;
            }

            parsingRoots = false;

            switch (text)
            {
                case "-name":
                    namePattern = CommandArguments.RequireString(arguments, ++index, "pattern");
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
                default:
                    throw new InvalidOperationException($"Unsupported find option '{text}'.");
            }
        }

        return new FindOptions(roots, namePattern, typeFilter, minDepth, maxDepth);
    }

    private sealed record FindOptions(
        IReadOnlyList<string> Roots,
        string? NamePattern,
        char? TypeFilter,
        int MinDepth,
        int MaxDepth);
}
