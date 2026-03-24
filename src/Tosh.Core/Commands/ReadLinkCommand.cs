namespace Tosh.Core.Commands;

public sealed class ReadLinkCommand : ShellCommand
{
    public ReadLinkCommand()
        : base("readlink", "Returns symbolic link targets.", "readlink [-f] <path> [path...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var resolveFinal = parsed.HasFlag("f");
        var paths = await ShellPathArguments.CollectAsync(context, parsed.Positionals, context.CancellationToken);

        if (paths.Count == 0)
        {
            throw new InvalidOperationException("readlink requires at least one path or pipeline input.");
        }

        foreach (var path in paths)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (resolveFinal)
            {
                yield return ResolveFinalPath(path);
                continue;
            }

            var entry = CreateFileSystemInfo(path);
            var target = entry.LinkTarget;

            if (target is null)
            {
                throw new InvalidOperationException($"Path '{path}' is not a symbolic link.");
            }

            yield return target;
        }
    }

    private static string ResolveFinalPath(string path)
    {
        var entry = CreateFileSystemInfo(path);
        var resolved = entry.ResolveLinkTarget(returnFinalTarget: true);
        return resolved?.FullName ?? Path.GetFullPath(path);
    }

    private static FileSystemInfo CreateFileSystemInfo(string path)
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
}
