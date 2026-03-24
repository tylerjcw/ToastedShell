namespace Tosh.Core.Commands;

public sealed class StatCommand : ShellCommand
{
    public StatCommand()
        : base("stat", "Returns detailed metadata for one or more paths.", "stat <path> [path...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var paths = await ShellPathArguments.CollectAsync(context, context.Arguments, context.CancellationToken);

        if (paths.Count == 0)
        {
            throw new InvalidOperationException("stat requires at least one path or pipeline input.");
        }

        foreach (var path in paths)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(path))
            {
                yield return FileSystemEntry.From(new FileInfo(path), preferLongDisplay: true);
                continue;
            }

            if (Directory.Exists(path))
            {
                yield return FileSystemEntry.From(new DirectoryInfo(path), preferLongDisplay: true);
                continue;
            }

            throw new InvalidOperationException($"Path '{path}' does not exist.");
        }
    }
}
