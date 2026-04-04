namespace Tosh.Core.Commands;

public sealed class HeadCommand : ShellCommand
{
    public HeadCommand()
        : base("head", "Returns the first objects or first text lines.", "head [-n count] [-c bytes] [path ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var options = ParseArguments(context.Arguments, context.Runtime.CurrentDirectory);

        if (options.ByteCount is not null)
        {
            if (options.Paths.Count == 0)
            {
                throw new InvalidOperationException("head -c requires at least one file path.");
            }

            foreach (var path in options.Paths)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                if (!File.Exists(path))
                {
                    throw new InvalidOperationException($"File '{path}' does not exist.");
                }

                var buffer = new byte[options.ByteCount.Value];
                await using var stream = File.OpenRead(path);
                var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(options.ByteCount.Value, stream.Length)), context.CancellationToken);
                yield return new ShellTextLine(System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead));
            }

            yield break;
        }

        if (options.Paths.Count > 0)
        {
            var lines = await TextInputUtilities.ReadLinesFromFilesAsync(options.Paths, context.CancellationToken);

            foreach (var line in lines.Take(options.Count))
            {
                yield return new ShellTextLine(line.Text);
            }

            yield break;
        }

        var items = await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);

        foreach (var item in items.Take(options.Count))
        {
            yield return item;
        }
    }

    private static HeadOptions ParseArguments(IReadOnlyList<object?> arguments, string currentDirectory)
    {
        var count = 10;
        long? byteCount = null;
        var pathArguments = new List<object?>();

        for (var index = 0; index < arguments.Count; index++)
        {
            var text = arguments[index]?.ToString();

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            switch (text)
            {
                case "-n" or "--lines":
                    count = CommandArguments.RequireConverted<int>(arguments, ++index, "count");
                    continue;
                case "-c" or "--bytes":
                    byteCount = CommandArguments.RequireConverted<long>(arguments, ++index, "bytes");
                    continue;
            }

            if (text.StartsWith("-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unsupported head option '{text}'.");
            }

            pathArguments.Add(arguments[index]);
        }

        return new HeadOptions(count, byteCount, ShellPathArguments.ExpandMany(currentDirectory, pathArguments));
    }

    private sealed record HeadOptions(int Count, long? ByteCount, IReadOnlyList<string> Paths);
}
