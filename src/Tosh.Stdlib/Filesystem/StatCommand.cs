using Tosh.Runtime;

namespace Tosh.Stdlib.Filesystem;

[CommandCategory("Filesystem")]
[CommandArgument("path", "One or more paths to inspect.", TypeName = "path-like")]
[CommandOption("-L", "Dereference symlinks before reading metadata.")]
[CommandOption("-f", "Return filesystem usage information for the containing mount instead of file-entry metadata.")]
[CommandOption("--show <columns>", "Select which properties are rendered.")]
[CommandOption("--hide <columns>", "Hide specific properties from the output.")]
[CommandOption("--show-all", "Display every available column.")]
[CommandExample("stat ./README.md")]
[CommandExample("stat -L ./link.txt", Title = "Dereference symlink")]
[CommandExample("stat -f . | get { RequestedPath, FileSystem, MountedOn }", Title = "Filesystem mode")]
[CommandOutput("Typed filesystem-entry metadata, or filesystem-usage objects in -f mode.")]
[PipelineInput(AcceptsList = true, Description = "Uses piped path-like values when explicit paths are omitted.")]
public sealed class StatCommand : ShellCommand
{
    public StatCommand()
        : base("stat", "Returns detailed metadata for one or more paths.", "stat [-L] [-f|--filesystem] [--show columns] [--hide columns] [--show-all] <path> [path...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var selection = CommandDisplaySelectionParser.Parse(context.Arguments);
        var options = ParseOptions(selection.RemainingArguments);
        var paths = await ShellPathArguments.CollectAsync(context, options.Paths, context.CancellationToken);

        if (paths.Count == 0)
        {
            throw new InvalidOperationException("stat requires at least one path or pipeline input.");
        }

        if (options.FileSystemMode)
        {
            var entries = UnixSystemServices.GetFileSystemUsage();

            foreach (var path in paths)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    throw new InvalidOperationException($"Path '{path}' does not exist.");
                }

                var match = FileSystemUsageUtilities.FindContainingMount(entries, path);

                if (match is null)
                {
                    throw new InvalidOperationException($"Could not resolve filesystem information for '{path}'.");
                }

                yield return CommandDisplaySelectionParser.Apply(
                    context.Runtime,
                    selection.Selection,
                    match with { RequestedPath = path });
            }

            yield break;
        }

        foreach (var path in paths)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var entry = ResolveEntry(path);
            var resolved = options.DereferenceLinks ? Dereference(entry) : entry;
            yield return CommandDisplaySelectionParser.Apply(
                context.Runtime,
                selection.Selection,
                FileSystemEntry.From(resolved, preferLongDisplay: true));
        }
    }

    private static FileSystemInfo ResolveEntry(string path)
    {
        if (File.Exists(path))
        {
            return new FileInfo(path);
        }

        if (Directory.Exists(path))
        {
            return new DirectoryInfo(path);
        }

        throw new InvalidOperationException($"Path '{path}' does not exist.");
    }

    private static FileSystemInfo Dereference(FileSystemInfo entry)
    {
        try
        {
            return entry.ResolveLinkTarget(returnFinalTarget: true) ?? entry;
        }
        catch
        {
            return entry;
        }
    }

    private static StatOptions ParseOptions(IReadOnlyList<object?> arguments)
    {
        var options = new StatOptions();
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
                switch (text)
                {
                    case "--dereference":
                        options.DereferenceLinks = true;
                        continue;
                    case "--filesystem":
                        options.FileSystemMode = true;
                        continue;
                    default:
                        throw new InvalidOperationException($"Unsupported stat option '{text}'.");
                }
            }

            if (text.StartsWith("-", StringComparison.Ordinal) && text.Length > 1)
            {
                foreach (var option in text[1..])
                {
                    switch (option)
                    {
                        case 'L':
                            options.DereferenceLinks = true;
                            break;
                        case 'f':
                            options.FileSystemMode = true;
                            break;
                        default:
                            throw new InvalidOperationException($"Unsupported stat option '-{option}'.");
                    }
                }

                continue;
            }

            options.Paths.Add(argument);
        }

        return options;
    }

    private sealed class StatOptions
    {
        public List<object?> Paths { get; } = [];

        public bool DereferenceLinks { get; set; }

        public bool FileSystemMode { get; set; }
    }
}
