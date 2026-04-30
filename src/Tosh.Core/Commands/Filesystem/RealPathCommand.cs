namespace Tosh.Core.Commands.Filesystem;

[Stdlib(StdlibCategory.Filesystem)]
[CommandCategory("Filesystem")]
[CommandArgument("path", "One or more paths to resolve.", TypeName = "path-like")]
[CommandExample("realpath ./relative/path")]
[CommandOutput("Returns fully resolved absolute path strings.")]
public sealed class RealPathCommand : ShellCommand
{
    public RealPathCommand()
        : base("realpath", "Returns fully resolved absolute paths.", "realpath <path> [path...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var paths = await ShellPathArguments.CollectAsync(context, context.Arguments, context.CancellationToken);

        if (paths.Count == 0)
        {
            throw new InvalidOperationException("realpath requires at least one path or pipeline input.");
        }

        foreach (var path in paths)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(path) && !Directory.Exists(path))
            {
                throw new InvalidOperationException($"Path '{path}' does not exist.");
            }

            if (File.Exists(path))
            {
                var file = new FileInfo(path);
                var resolved = file.ResolveLinkTarget(returnFinalTarget: true);
                yield return resolved?.FullName ?? file.FullName;
                continue;
            }

            var directory = new DirectoryInfo(path);
            var resolvedDirectory = directory.ResolveLinkTarget(returnFinalTarget: true);
            yield return resolvedDirectory?.FullName ?? directory.FullName;
        }
    }
}
