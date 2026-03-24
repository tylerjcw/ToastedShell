namespace Tosh.Core.Commands;

public sealed class CatCommand : ShellCommand
{
    public CatCommand()
        : base("cat", "Reads one or more files and emits their contents as strings.", "cat <path> [path...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var paths = await ShellPathArguments.CollectAsync(context, parsed.Positionals, context.CancellationToken);

        if (paths.Count == 0)
        {
            throw new InvalidOperationException("cat requires at least one path or pipeline input.");
        }

        foreach (var path in paths)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"File '{path}' does not exist.");
            }

            using var reader = new StreamReader(path);
            string? line;

            while ((line = await reader.ReadLineAsync(context.CancellationToken)) is not null)
            {
                yield return new ShellTextLine(line);
            }
        }
    }
}
