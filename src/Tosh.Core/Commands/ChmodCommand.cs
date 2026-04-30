namespace Tosh.Core.Commands;

[Stdlib(StdlibCategory.Filesystem)]
[CommandCategory("Filesystem")]
[CommandArgument("mode", "Permission mode string (e.g. 755, u+rw, a+x).")]
[CommandArgument("path", "One or more files or directories.", TypeName = "path-like")]
[CommandOption("-R", "Operate recursively on directories.")]
[CommandExample("chmod +x script.sh")]
[CommandExample("chmod 755 script.sh", Title = "Octal mode")]
[CommandExample("chmod u+rw,go-w file.txt", Title = "Symbolic mode")]
[CommandExample("chmod -R a+r ./docs", Title = "Recursive")]
[CommandOutput("Returns FileSystemEntry objects with long display for each changed path.")]
public sealed class ChmodCommand : ShellCommand
{
    private static readonly UnixFileMode WindowsReadBits =
        UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private static readonly UnixFileMode WindowsWriteBits =
        UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;

    private static readonly UnixFileMode WindowsExecuteBits =
        UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    public ChmodCommand()
        : base("chmod", "Changes file permission bits.", "chmod [-R] <mode> <path> [path...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var recursive = parsed.HasFlag("R", "recursive");

        if (parsed.Positionals.Count == 0)
        {
            throw new InvalidOperationException("chmod requires a mode and at least one path.");
        }

        var modeText = parsed.Positionals[0]?.ToString()
                       ?? throw new InvalidOperationException("Missing required argument: mode.");
        var paths = parsed.Positionals.Count > 1
            ? ShellPathArguments.ExpandMany(context.Runtime.CurrentDirectory, parsed.Positionals.Skip(1).ToArray())
            : await ShellPathArguments.CollectAsync(context, Array.Empty<object?>(), context.CancellationToken);

        if (paths.Count == 0)
        {
            throw new InvalidOperationException("chmod requires at least one path.");
        }

        foreach (var path in paths)
        {
            foreach (var target in EnumerateTargets(path, recursive))
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var currentMode = GetCurrentMode(target);
                var newMode = UnixFileModeParser.Parse(modeText, currentMode);
                ApplyMode(target, newMode);
                yield return FileSystemEntry.From(CreateFileSystemInfo(target), preferLongDisplay: true);
            }
        }
    }

    private static UnixFileMode GetCurrentMode(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return File.GetUnixFileMode(path);
        }

        var attributes = File.GetAttributes(path);
        var mode = WindowsReadBits;

        if (!attributes.HasFlag(FileAttributes.ReadOnly))
        {
            mode |= WindowsWriteBits;
        }

        if (Directory.Exists(path) || IsWindowsExecutableName(path))
        {
            mode |= WindowsExecuteBits;
        }

        return mode;
    }

    private static void ApplyMode(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, mode);
            return;
        }

        var attributes = File.GetAttributes(path);
        var writable = (mode & WindowsWriteBits) != 0;

        if (writable)
        {
            attributes &= ~FileAttributes.ReadOnly;
        }
        else
        {
            attributes |= FileAttributes.ReadOnly;
        }

        File.SetAttributes(path, attributes);
    }

    private static bool IsWindowsExecutableName(string path)
    {
        var extension = Path.GetExtension(path);

        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".sh", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateTargets(string path, bool recursive)
    {
        if (File.Exists(path))
        {
            yield return path;
            yield break;
        }

        if (!Directory.Exists(path))
        {
            throw new InvalidOperationException($"Path '{path}' does not exist.");
        }

        yield return path;

        if (!recursive)
        {
            yield break;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
        {
            yield return entry;
        }
    }

    private static FileSystemInfo CreateFileSystemInfo(string path)
    {
        return Directory.Exists(path)
            ? new DirectoryInfo(path)
            : new FileInfo(path);
    }
}
