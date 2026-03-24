namespace Tosh.Core.Commands;

public sealed class TailCommand : ShellCommand
{
    public TailCommand()
        : base("tail", "Returns the last objects or last text lines.", "tail [-n count] [path ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var (count, paths) = ParseArguments(context.Arguments, context.Runtime.CurrentDirectory);

        if (paths.Count > 0)
        {
            var lines = await TextInputUtilities.ReadLinesFromFilesAsync(paths, context.CancellationToken);

            foreach (var line in lines.TakeLast(count))
            {
                yield return new ShellTextLine(line.Text);
            }

            yield break;
        }

        var items = await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);

        foreach (var item in items.TakeLast(count))
        {
            yield return item;
        }
    }

    private static (int Count, IReadOnlyList<string> Paths) ParseArguments(IReadOnlyList<object?> arguments, string currentDirectory)
    {
        var count = 10;
        var paths = new List<string>();

        for (var index = 0; index < arguments.Count; index++)
        {
            var text = arguments[index]?.ToString();

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (text is "-n" or "--lines")
            {
                count = CommandArguments.RequireConverted<int>(arguments, ++index, "count");
                continue;
            }

            if (text.StartsWith("-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unsupported tail option '{text}'.");
            }

            paths.Add(PathUtilities.ResolvePath(currentDirectory, text));
        }

        return (count, paths);
    }
}
