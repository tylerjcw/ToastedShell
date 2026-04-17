namespace Tosh.Core.Commands;

[CommandCategory("Filesystem")]
[CommandArgument("source ...", "One or more source paths to move.", TypeName = "path-like")]
[CommandArgument("destination", "The destination path or directory.", Required = false, TypeName = "path-like")]
[CommandOption("-n", "Do not overwrite existing files.")]
[CommandOption("-u", "Only move when the source is newer than the destination.")]
[CommandOption("-f", "Force overwrite for file targets, clearing a previous `-n`.")]
[CommandOption("-t <directory>", "Use an explicit destination directory.")]
[CommandOption("-T", "Treat the destination as a normal path, not a target directory.")]
[CommandOption("-v", "Explain what is being done.")]
[CommandExample("mv old.txt new.txt")]
[CommandExample("mv -n *.txt archive/")]
[CommandExample("mv -t archive alpha.txt beta.txt")]
[CommandNote("Mv now overwrites existing file targets by default, closer to Unix `mv`. `-n`, `-u`, `-t`, and `-T` are available to control that behavior explicitly.")]
[CommandOutput("Returns FileInfo or DirectoryInfo objects for the moved targets.")]
[CommandSideEffects(WritesFiles = true)]
[PipelineInput(Description = "The current `mv` implementation is explicit-arg-first and does not consume pipeline input.")]
public sealed class MoveItemCommand : ShellCommand
{
    public MoveItemCommand()
        : base("mv", "Moves or renames files and directories.", "mv [-nufTiv] [-t directory] <source> [source ...] <destination>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var options = ParseOptions(context.Arguments, context.Runtime.CurrentDirectory);

        if (options.TargetDirectory is null && options.Positionals.Count < 2)
        {
            throw new InvalidOperationException("mv requires at least one source path and a destination path.");
        }

        if (options.TargetDirectory is not null && options.Positionals.Count < 1)
        {
            throw new InvalidOperationException("mv -t requires at least one source path.");
        }

        var sourceArguments = options.TargetDirectory is null
            ? options.Positionals.Take(options.Positionals.Count - 1).ToArray()
            : options.Positionals.ToArray();
        var sources = ShellPathArguments.ExpandMany(context.Runtime.CurrentDirectory, sourceArguments);
        var destination = options.TargetDirectory is not null
            ? ShellPathArguments.Resolve(context.Runtime.CurrentDirectory, options.TargetDirectory)
            : ShellPathArguments.Resolve(context.Runtime.CurrentDirectory, options.Positionals[^1]);

        if (sources.Count == 0)
        {
            throw new InvalidOperationException("mv requires at least one source path.");
        }

        if (sources.Count > 1 && !Directory.Exists(destination))
        {
            throw new InvalidOperationException("When moving multiple sources, the destination must be an existing directory or use '-t'.");
        }

        foreach (var source in sources)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var targetPath = DetermineTargetPath(source, destination, options.NoTargetDirectory);

            if (File.Exists(source))
            {
                if (ShouldSkipMove(source, targetPath, options.NoClobber, options.Update))
                {
                    continue;
                }

                if (options.Interactive && File.Exists(targetPath) && !ConfirmOverwrite(context, targetPath))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? context.Runtime.CurrentDirectory);
                File.Move(source, targetPath, overwrite: true);

                if (options.Verbose)
                {
                    Console.Error.WriteLine($"renamed '{source}' -> '{targetPath}'");
                }

                yield return new FileInfo(targetPath);
                continue;
            }

            if (Directory.Exists(source))
            {
                if (File.Exists(targetPath))
                {
                    if (options.NoClobber || options.Update)
                    {
                        continue;
                    }

                    throw new InvalidOperationException($"Cannot overwrite file '{targetPath}' with directory '{source}'.");
                }

                if (Directory.Exists(targetPath))
                {
                    if (options.NoClobber || options.Update)
                    {
                        continue;
                    }

                    throw new InvalidOperationException($"Target directory '{targetPath}' already exists.");
                }

                Directory.Move(source, targetPath);

                if (options.Verbose)
                {
                    Console.Error.WriteLine($"renamed '{source}' -> '{targetPath}'");
                }

                yield return new DirectoryInfo(targetPath);
                continue;
            }

            throw new InvalidOperationException($"Source path '{source}' does not exist.");
        }
    }

    private static MoveOptions ParseOptions(IReadOnlyList<object?> arguments, string currentDirectory)
    {
        var positionals = new List<object?>();
        object? targetDirectory = null;
        var noClobber = false;
        var update = false;
        var noTargetDirectory = false;
        var interactive = false;
        var verbose = false;
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

            if (text.StartsWith("--target-directory=", StringComparison.Ordinal))
            {
                targetDirectory = text["--target-directory=".Length..];
                continue;
            }

            if (text is "-n" or "--no-clobber")
            {
                noClobber = true;
                continue;
            }

            if (text is "-u" or "--update")
            {
                update = true;
                continue;
            }

            if (text is "-f" or "--force")
            {
                noClobber = false;
                continue;
            }

            if (text is "-i" or "--interactive")
            {
                interactive = true;
                continue;
            }

            if (text is "-v" or "--verbose")
            {
                verbose = true;
                continue;
            }

            if (text is "-T" or "--no-target-directory")
            {
                noTargetDirectory = true;
                continue;
            }

            if (text is "-t" or "--target-directory")
            {
                if (++index >= arguments.Count)
                {
                    throw new InvalidOperationException("mv option '-t' requires a destination directory.");
                }

                targetDirectory = arguments[index];
                continue;
            }

            if (TryParseShortOptionCluster(
                    text,
                    arguments,
                    ref index,
                    ref noClobber,
                    ref update,
                    ref noTargetDirectory,
                    ref interactive,
                    ref verbose,
                    ref targetDirectory))
            {
                continue;
            }

            positionals.Add(argument);
        }

        if (targetDirectory is not null && noTargetDirectory)
        {
            throw new InvalidOperationException("mv options '-t/--target-directory' and '-T/--no-target-directory' cannot be used together.");
        }

        if (targetDirectory is not null)
        {
            var resolved = ShellPathArguments.Resolve(currentDirectory, targetDirectory);

            if (!Directory.Exists(resolved))
            {
                throw new InvalidOperationException($"mv target directory '{resolved}' does not exist.");
            }
        }

        return new MoveOptions(noClobber, update, noTargetDirectory, interactive, verbose, targetDirectory, positionals);
    }

    private static bool TryParseShortOptionCluster(
        string text,
        IReadOnlyList<object?> arguments,
        ref int index,
        ref bool noClobber,
        ref bool update,
        ref bool noTargetDirectory,
        ref bool interactive,
        ref bool verbose,
        ref object? targetDirectory)
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
                case 'n':
                    noClobber = true;
                    break;
                case 'u':
                    update = true;
                    break;
                case 'f':
                    noClobber = false;
                    break;
                case 'i':
                    interactive = true;
                    break;
                case 'v':
                    verbose = true;
                    break;
                case 'T':
                    noTargetDirectory = true;
                    break;
                case 't':
                    {
                        targetDirectory = flagIndex + 1 < text.Length
                            ? text[(flagIndex + 1)..]
                            : RequireTargetDirectory(arguments, ref index);
                        return true;
                    }
                default:
                    return false;
            }
        }

        return true;
    }

    private static object? RequireTargetDirectory(IReadOnlyList<object?> arguments, ref int index)
    {
        if (++index >= arguments.Count)
        {
            throw new InvalidOperationException("mv option '-t' requires a destination directory.");
        }

        return arguments[index];
    }

    private static string DetermineTargetPath(string source, string destination, bool noTargetDirectory)
    {
        return !noTargetDirectory && Directory.Exists(destination)
            ? Path.Combine(destination, Path.GetFileName(source))
            : destination;
    }

    private static bool ShouldSkipMove(string source, string targetPath, bool noClobber, bool update)
    {
        if (!File.Exists(targetPath))
        {
            return false;
        }

        if (noClobber)
        {
            return true;
        }

        if (update)
        {
            return File.GetLastWriteTimeUtc(source) <= File.GetLastWriteTimeUtc(targetPath);
        }

        return false;
    }

    private static bool ConfirmOverwrite(CommandContext context, string path)
    {
        var provider = context.Runtime.InlinePrompts;

        if (provider is null)
        {
            return true;
        }

        var name = Path.GetFileName(path);
        return provider.Confirm($"mv: overwrite '{name}'?", false) ?? false;
    }

    private sealed record MoveOptions(
        bool NoClobber,
        bool Update,
        bool NoTargetDirectory,
        bool Interactive,
        bool Verbose,
        object? TargetDirectory,
        IReadOnlyList<object?> Positionals);
}
