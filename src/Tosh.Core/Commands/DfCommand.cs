namespace Tosh.Core.Commands;

public sealed class DfCommand : ShellCommand
{
    public DfCommand(string name = "df")
        : base(name, "Returns mounted file system usage information.", $"{name} [path ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var entries = UnixSystemServices.GetFileSystemUsage();

        if (context.Arguments.Count == 0)
        {
            foreach (var entry in entries)
            {
                yield return entry;
            }

            yield break;
        }

        var yieldedMounts = new HashSet<string>(StringComparer.Ordinal);

        foreach (var argument in context.Arguments)
        {
            var path = argument?.ToString();

            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var resolvedPath = PathUtilities.ResolvePath(context.Runtime.CurrentDirectory, path);

            if (!File.Exists(resolvedPath) && !Directory.Exists(resolvedPath))
            {
                throw new InvalidOperationException($"Path '{resolvedPath}' does not exist.");
            }

            var match = entries
                .Where(entry => PathIsWithinMount(resolvedPath, entry.MountedOn))
                .OrderByDescending(entry => entry.MountedOn.Length)
                .FirstOrDefault();

            if (match is not null && yieldedMounts.Add(match.MountedOn))
            {
                yield return match;
            }
        }
    }

    private static bool PathIsWithinMount(string path, string mountPoint)
    {
        if (string.Equals(path, mountPoint, StringComparison.Ordinal))
        {
            return true;
        }

        if (mountPoint == Path.DirectorySeparatorChar.ToString())
        {
            return path.StartsWith(mountPoint, StringComparison.Ordinal);
        }

        return path.StartsWith(mountPoint + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
