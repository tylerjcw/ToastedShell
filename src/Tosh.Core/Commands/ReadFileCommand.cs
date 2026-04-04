namespace Tosh.Core.Commands;

public sealed class ReadFileCommand : ShellCommand
{
    public ReadFileCommand()
        : base("read-file", "Reads one or more files and returns each file as a single string value.", "read-file <path> [path...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var paths = await ShellPathArguments.CollectAsync(context, parsed.Positionals, context.CancellationToken);

        if (paths.Count == 0)
        {
            throw new InvalidOperationException("read-file requires at least one path or pipeline input.");
        }

        foreach (var path in paths)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            yield return await FileIoUtilities.ReadAllTextAsync(path, context.CancellationToken);
        }
    }
}
