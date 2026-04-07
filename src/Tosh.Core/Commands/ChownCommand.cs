namespace Tosh.Core.Commands;

[CommandCategory("Filesystem")]
[CommandArgument("owner[:group]", "Owner and optional group specification.")]
[CommandArgument("path", "One or more files or directories.", TypeName = "path-like")]
[CommandOption("-R", "Operate recursively on directories.")]
[CommandExample("chown user:group file.txt")]
[CommandExample("chown -R www-data /var/www", Title = "Recursive change")]
[CommandOutput("Returns FileSystemEntry objects for each changed path.")]
public sealed class ChownCommand : ShellCommand
{
    public ChownCommand()
        : base("chown", "Changes file owner and group.", "chown [-R] <owner>[:group] <path> [path...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("chown is not supported on Windows.");
        }

        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var recursive = parsed.HasFlag("R", "recursive");

        if (parsed.Positionals.Count == 0)
        {
            throw new InvalidOperationException("chown requires an owner specification and at least one path.");
        }

        var spec = CommandArguments.RequireString(parsed.Positionals, 0, "owner");
        var (uid, gid) = ParseOwnership(spec);
        var paths = parsed.Positionals.Count > 1
            ? ShellPathArguments.ExpandMany(context.Runtime.CurrentDirectory, parsed.Positionals.Skip(1).ToArray())
            : await ShellPathArguments.CollectAsync(context, Array.Empty<object?>(), context.CancellationToken);

        if (paths.Count == 0)
        {
            throw new InvalidOperationException("chown requires at least one path.");
        }

        foreach (var path in paths)
        {
            foreach (var target in EnumerateTargets(path, recursive))
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                UnixOwnershipUtilities.ChangeOwnership(target, uid, gid);
                yield return FileSystemEntry.From(CreateFileSystemInfo(target), preferLongDisplay: true);
            }
        }
    }

    private static (uint? Uid, uint? Gid) ParseOwnership(string spec)
    {
        var parts = spec.Split(':', 2);
        var uid = UnixOwnershipUtilities.ResolveUserId(parts[0]);

        if (parts[0].Length > 0 && uid is null)
        {
            throw new InvalidOperationException($"Unknown user '{parts[0]}'.");
        }

        uint? gid = null;

        if (parts.Length == 2)
        {
            gid = UnixOwnershipUtilities.ResolveGroupId(parts[1]);

            if (parts[1].Length > 0 && gid is null)
            {
                throw new InvalidOperationException($"Unknown group '{parts[1]}'.");
            }
        }

        return (uid, gid);
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
