namespace Tosh.Core.Commands.Filesystem;

[Stdlib(StdlibCategory.Filesystem)]
[CommandCategory("Filesystem")]
[CommandArgument("path ...", "One or more filesystem paths to create or timestamp.", TypeName = "path-like")]
[CommandOption("-a", "Update only the access time.")]
[CommandOption("-m", "Update only the modification time.")]
[CommandOption("-c", "Do not create missing files.")]
[CommandOption("-d <time>", "Use an explicit timestamp value.")]
[CommandOption("-r <path>", "Copy the timestamp from another file or directory.")]
[CommandExample("touch notes.txt")]
[CommandExample("touch -c -a notes.txt")]
[CommandExample("touch -d 2026-03-28T12:00:00 notes.txt")]
[CommandNote("Touch now supports `-a`, `-m`, `-c`, `-d`, and `-r`, plus grouped short flags like `-am`.")]
[CommandOutput("Returns updated FileInfo or DirectoryInfo objects for the paths it touched.")]
[CommandSideEffects(WritesFiles = true)]
[PipelineInput(AcceptsList = true, Description = "Consumes piped path-like input when explicit paths are omitted.")]
public sealed class TouchCommand : ShellCommand
{
    public TouchCommand()
        : base("touch", "Creates files or updates access and modification timestamps.", "touch [-acm] [-d time|-r file] [-c] <path> [path...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var options = ParseOptions(context.Arguments, context.Runtime.CurrentDirectory);
        var paths = await ShellPathArguments.CollectAsync(context, options.Positionals, context.CancellationToken);

        if (paths.Count == 0)
        {
            throw new InvalidOperationException("touch requires at least one path or pipeline input.");
        }

        var timestampUtc = options.Timestamp?.UtcDateTime ?? DateTime.UtcNow;
        var updateAccess = options.AccessOnly || !options.ModificationOnly;
        var updateModified = options.ModificationOnly || !options.AccessOnly;

        foreach (var path in paths)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(path) && !Directory.Exists(path))
            {
                if (options.NoCreate)
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? context.Runtime.CurrentDirectory);

                await using (var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
                {
                    await stream.FlushAsync(context.CancellationToken);
                }
            }

            if (Directory.Exists(path))
            {
                if (updateAccess)
                {
                    Directory.SetLastAccessTimeUtc(path, timestampUtc);
                }

                if (updateModified)
                {
                    Directory.SetLastWriteTimeUtc(path, timestampUtc);
                }

                yield return new DirectoryInfo(path);
                continue;
            }

            if (updateAccess)
            {
                File.SetLastAccessTimeUtc(path, timestampUtc);
            }

            if (updateModified)
            {
                File.SetLastWriteTimeUtc(path, timestampUtc);
            }

            yield return new FileInfo(path);
        }
    }

    private static TouchOptions ParseOptions(IReadOnlyList<object?> arguments, string currentDirectory)
    {
        var positionals = new List<object?>();
        var accessOnly = false;
        var modificationOnly = false;
        var noCreate = false;
        DateTimeOffset? timestamp = null;
        var parseOptions = true;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];

            if (!parseOptions || argument is not string text || text.Length == 0)
            {
                positionals.Add(argument);
                continue;
            }

            if (text == "--")
            {
                parseOptions = false;
                continue;
            }

            if (TryConsumeLongOptionWithValue(
                    text,
                    "--reference",
                    currentDirectory,
                    arguments,
                    ref index,
                    ref timestamp,
                    reference: true))
            {
                continue;
            }

            if (TryConsumeLongOptionWithValue(
                    text,
                    "--date",
                    currentDirectory,
                    arguments,
                    ref index,
                    ref timestamp,
                    reference: false))
            {
                continue;
            }

            if (text is "-a" or "--access")
            {
                accessOnly = true;
                continue;
            }

            if (text is "-m" or "--modification")
            {
                modificationOnly = true;
                continue;
            }

            if (text is "-c" or "--no-create")
            {
                noCreate = true;
                continue;
            }

            if (text is "-r" or "--reference")
            {
                if (timestamp is not null)
                {
                    throw new InvalidOperationException("touch accepts either '-d/--date' or '-r/--reference', but not both.");
                }

                if (++index >= arguments.Count)
                {
                    throw new InvalidOperationException("touch option '-r' requires a reference path.");
                }

                var referencePath = ShellPathArguments.Resolve(currentDirectory, arguments[index]);
                timestamp = GetReferenceTimestamp(referencePath);
                continue;
            }

            if (text is "-d" or "--date")
            {
                if (timestamp is not null)
                {
                    throw new InvalidOperationException("touch accepts either '-d/--date' or '-r/--reference', but not both.");
                }

                if (++index >= arguments.Count)
                {
                    throw new InvalidOperationException("touch option '-d' requires a timestamp value.");
                }

                if (!TypeConversion.TryConvert(arguments[index], typeof(DateTimeOffset), out var converted) || converted is not DateTimeOffset parsedTimestamp)
                {
                    throw new InvalidOperationException("touch option '-d' expects a DateTime, DateTimeOffset, or parseable date/time string.");
                }

                timestamp = parsedTimestamp;
                continue;
            }

            if (TryParseShortOptionCluster(
                    text,
                    currentDirectory,
                    arguments,
                    ref index,
                    ref accessOnly,
                    ref modificationOnly,
                    ref noCreate,
                    ref timestamp))
            {
                continue;
            }

            positionals.Add(argument);
        }

        return new TouchOptions(accessOnly, modificationOnly, noCreate, timestamp, positionals);
    }

    private static bool TryParseShortOptionCluster(
        string text,
        string currentDirectory,
        IReadOnlyList<object?> arguments,
        ref int index,
        ref bool accessOnly,
        ref bool modificationOnly,
        ref bool noCreate,
        ref DateTimeOffset? timestamp)
    {
        if (!text.StartsWith("-", StringComparison.Ordinal) ||
            text.StartsWith("--", StringComparison.Ordinal) ||
            text.Length <= 2)
        {
            return false;
        }

        for (var flagIndex = 1; flagIndex < text.Length; flagIndex++)
        {
            switch (text[flagIndex])
            {
                case 'a':
                    accessOnly = true;
                    break;
                case 'm':
                    modificationOnly = true;
                    break;
                case 'c':
                    noCreate = true;
                    break;
                case 'r':
                case 'd':
                    {
                        if (timestamp is not null)
                        {
                            throw new InvalidOperationException("touch accepts either '-d/--date' or '-r/--reference', but not both.");
                        }

                        var inlineValue = flagIndex + 1 < text.Length
                            ? text[(flagIndex + 1)..]
                            : null;
                        var value = !string.IsNullOrEmpty(inlineValue)
                            ? inlineValue
                            : RequireOptionValue(arguments, ref index, $"-{text[flagIndex]}");

                        timestamp = text[flagIndex] == 'r'
                            ? GetReferenceTimestamp(ShellPathArguments.Resolve(currentDirectory, value))
                            : ParseTimestamp(value);
                        return true;
                    }
                default:
                    return false;
            }
        }

        return true;
    }

    private static bool TryConsumeLongOptionWithValue(
        string text,
        string optionName,
        string currentDirectory,
        IReadOnlyList<object?> arguments,
        ref int index,
        ref DateTimeOffset? timestamp,
        bool reference)
    {
        if (!text.StartsWith(optionName + "=", StringComparison.Ordinal))
        {
            return false;
        }

        if (timestamp is not null)
        {
            throw new InvalidOperationException("touch accepts either '-d/--date' or '-r/--reference', but not both.");
        }

        var value = text[(optionName.Length + 1)..];

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"touch option '{optionName}' requires a value.");
        }

        timestamp = reference
            ? GetReferenceTimestamp(ShellPathArguments.Resolve(currentDirectory, value))
            : ParseTimestamp(value);
        return true;
    }

    private static object? RequireOptionValue(IReadOnlyList<object?> arguments, ref int index, string optionName)
    {
        if (++index >= arguments.Count)
        {
            throw new InvalidOperationException($"touch option '{optionName}' requires a value.");
        }

        return arguments[index];
    }

    private static DateTimeOffset ParseTimestamp(object? value)
    {
        if (!TypeConversion.TryConvert(value, typeof(DateTimeOffset), out var converted) || converted is not DateTimeOffset parsedTimestamp)
        {
            throw new InvalidOperationException("touch option '-d' expects a DateTime, DateTimeOffset, or parseable date/time string.");
        }

        return parsedTimestamp;
    }

    private static DateTimeOffset GetReferenceTimestamp(string referencePath)
    {
        if (File.Exists(referencePath))
        {
            return new DateTimeOffset(File.GetLastWriteTimeUtc(referencePath), TimeSpan.Zero);
        }

        if (Directory.Exists(referencePath))
        {
            return new DateTimeOffset(Directory.GetLastWriteTimeUtc(referencePath), TimeSpan.Zero);
        }

        throw new InvalidOperationException($"Reference path '{referencePath}' does not exist.");
    }

    private sealed record TouchOptions(
        bool AccessOnly,
        bool ModificationOnly,
        bool NoCreate,
        DateTimeOffset? Timestamp,
        IReadOnlyList<object?> Positionals);
}
