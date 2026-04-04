namespace Tosh.Core.Commands;

public sealed class CopyItemCommand : ShellCommand
{
    public CopyItemCommand()
        : base("cp", "Copies a file or directory.", "cp [-r] [-f] [-n] [-p] [-u] <source> [source ...] <destination>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var recursive = parsed.HasFlag("r", "R", "recursive");
        var force = parsed.HasFlag("f", "force");
        var noClobber = parsed.HasFlag("n", "no-clobber");
        var preserve = parsed.HasFlag("p", "preserve");
        var update = parsed.HasFlag("u", "update");

        if (parsed.Positionals.Count < 2)
        {
            throw new InvalidOperationException("cp requires at least one source path and a destination path.");
        }

        var sources = ShellPathArguments.ExpandMany(context.Runtime.CurrentDirectory, parsed.Positionals.Take(parsed.Positionals.Count - 1).ToArray());
        var destinationMatches = ShellPathArguments.Expand(context.Runtime.CurrentDirectory, parsed.Positionals[^1]);

        if (destinationMatches.Count != 1)
        {
            throw new InvalidOperationException("cp destination must resolve to exactly one path.");
        }

        var destination = destinationMatches[0];

        if (sources.Count > 1 && !Directory.Exists(destination))
        {
            throw new InvalidOperationException("When copying multiple sources, the destination must be an existing directory.");
        }

        foreach (var source in sources)
        {
            if (File.Exists(source))
            {
                var targetPath = ResolveCopyDestination(source, destination);

                if (ShouldSkipCopy(source, targetPath, noClobber, update))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.Copy(source, targetPath, overwrite: force || !noClobber);

                if (preserve)
                {
                    PreserveAttributes(source, targetPath);
                }

                yield return new FileInfo(targetPath);
                continue;
            }

            if (Directory.Exists(source))
            {
                if (!recursive)
                {
                    throw new InvalidOperationException("Copying directories requires -r.");
                }

                var targetPath = Directory.Exists(destination)
                    ? Path.Combine(destination, Path.GetFileName(source))
                    : destination;

                CopyDirectory(source, targetPath, force, noClobber, update, preserve);
                yield return new DirectoryInfo(targetPath);
                continue;
            }

            throw new InvalidOperationException($"Source path '{source}' does not exist.");
        }
    }

    private static bool ShouldSkipCopy(string source, string target, bool noClobber, bool update)
    {
        if (!File.Exists(target))
        {
            return false;
        }

        if (noClobber)
        {
            return true;
        }

        if (update)
        {
            return File.GetLastWriteTimeUtc(source) <= File.GetLastWriteTimeUtc(target);
        }

        return false;
    }

    private static void PreserveAttributes(string source, string target)
    {
        var sourceInfo = new FileInfo(source);
        var targetInfo = new FileInfo(target);
        targetInfo.LastWriteTimeUtc = sourceInfo.LastWriteTimeUtc;
        targetInfo.LastAccessTimeUtc = sourceInfo.LastAccessTimeUtc;
        targetInfo.CreationTimeUtc = sourceInfo.CreationTimeUtc;

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(target, File.GetUnixFileMode(source));
            }
            catch (IOException)
            {
                // Best-effort on permission preservation
            }
        }
    }

    private static string ResolveCopyDestination(string source, string destination)
    {
        return Directory.Exists(destination)
            ? Path.Combine(destination, Path.GetFileName(source))
            : destination;
    }

    private static void CopyDirectory(string source, string destination, bool force, bool noClobber, bool update, bool preserve)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            var targetFile = Path.Combine(destination, Path.GetFileName(file));

            if (!ShouldSkipCopy(file, targetFile, noClobber, update))
            {
                File.Copy(file, targetFile, overwrite: force || !noClobber);

                if (preserve)
                {
                    PreserveAttributes(file, targetFile);
                }
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var targetDirectory = Path.Combine(destination, Path.GetFileName(directory));
            CopyDirectory(directory, targetDirectory, force, noClobber, update, preserve);
        }
    }
}
