namespace Tosh.Core.Commands;

public sealed class TouchCommand : ShellCommand
{
    public TouchCommand()
        : base("touch", "Creates files or updates their timestamps.", "touch <path> [path...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var paths = await ShellPathArguments.CollectAsync(context, parsed.Positionals, context.CancellationToken);

        if (paths.Count == 0)
        {
            throw new InvalidOperationException("touch requires at least one path or pipeline input.");
        }

        foreach (var path in paths)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var now = DateTime.UtcNow;

            if (Directory.Exists(path))
            {
                Directory.SetLastWriteTimeUtc(path, now);
                yield return new DirectoryInfo(path);
                continue;
            }

            await using (var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
            {
                await stream.FlushAsync(context.CancellationToken);
            }

            File.SetLastWriteTimeUtc(path, now);
            yield return new FileInfo(path);
        }
    }
}
