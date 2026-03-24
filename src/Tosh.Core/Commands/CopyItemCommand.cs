namespace Tosh.Core.Commands;

public sealed class CopyItemCommand : ShellCommand
{
    public CopyItemCommand()
        : base("cp", "Copies a file or directory.", "cp [-r] <source> <destination>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var recursive = parsed.HasFlag("r", "R", "recursive");

        if (parsed.Positionals.Count != 2)
        {
            throw new InvalidOperationException("cp requires a source path and a destination path.");
        }

        var source = ShellPathArguments.Resolve(context.Runtime.CurrentDirectory, parsed.Positionals[0]);
        var destination = ShellPathArguments.Resolve(context.Runtime.CurrentDirectory, parsed.Positionals[1]);

        if (File.Exists(source))
        {
            var targetPath = ResolveCopyDestination(source, destination);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(source, targetPath, overwrite: false);
            yield return new FileInfo(targetPath);
            yield break;
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

            CopyDirectory(source, targetPath);
            yield return new DirectoryInfo(targetPath);
            yield break;
        }

        throw new InvalidOperationException($"Source path '{source}' does not exist.");
    }

    private static string ResolveCopyDestination(string source, string destination)
    {
        return Directory.Exists(destination)
            ? Path.Combine(destination, Path.GetFileName(source))
            : destination;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            var targetFile = Path.Combine(destination, Path.GetFileName(file));
            File.Copy(file, targetFile, overwrite: false);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var targetDirectory = Path.Combine(destination, Path.GetFileName(directory));
            CopyDirectory(directory, targetDirectory);
        }
    }
}
