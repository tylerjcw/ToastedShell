namespace Tosh.Core.Commands;

[Stdlib(StdlibCategory.Text)]
[CommandCategory("Text")]
[CommandArgument("path ...", "Optional files to read instead of pipeline input.", Required = false, TypeName = "path-like")]
[CommandOption("-n, --lines <count>", "Return this many lines or pipeline items. Defaults to 10.")]
[CommandOption("-c, --bytes <bytes>", "Return this many trailing bytes from each file. Requires file paths.")]
[CommandOption("-f, --follow", "Continue following a single file and emit newly appended lines.")]
[CommandExample("ls | tail -n 5", Title = "Take the last five pipeline items")]
[CommandExample("tail -n 50 app.log", Title = "Read the last fifty lines of a file")]
[CommandExample("tail -f app.log", Title = "Follow appended log lines")]
[CommandOutput("The last N items of the input stream (default 10), preserving order.")]
public sealed class TailCommand : ShellCommand
{
    public TailCommand()
        : base("tail", "Returns the last objects or last text lines.", "tail [-n count] [-c bytes] [-f] [path ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var options = ParseArguments(context.Arguments, context.Runtime.CurrentDirectory);

        if (options.Follow)
        {
            if (options.Paths.Count != 1)
            {
                throw new InvalidOperationException("tail -f requires exactly one file path.");
            }

            var path = options.Paths[0];

            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"File '{path}' does not exist.");
            }

            // Emit last N lines, then follow
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var initialContent = await reader.ReadToEndAsync(context.CancellationToken);
            var initialLines = initialContent.Split('\n');

            // Remove trailing empty entry from final newline
            if (initialLines.Length > 0 && initialLines[^1].Length == 0)
            {
                initialLines = initialLines[..^1];
            }

            foreach (var line in initialLines.TakeLast(options.Count))
            {
                yield return new ShellTextLine(line.TrimEnd('\r'));
            }

            // Follow: poll for new content
            while (!context.CancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(context.CancellationToken);

                if (line is not null)
                {
                    yield return new ShellTextLine(line);
                    continue;
                }

                try
                {
                    await Task.Delay(100, context.CancellationToken);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }
            }

            yield break;
        }

        if (options.ByteCount is not null)
        {
            foreach (var path in options.Paths)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var fileInfo = new FileInfo(path);

                if (!fileInfo.Exists)
                {
                    throw new InvalidOperationException($"File '{path}' does not exist.");
                }

                var bytesToRead = Math.Min(options.ByteCount.Value, fileInfo.Length);
                var offset = fileInfo.Length - bytesToRead;
                var buffer = new byte[(int)bytesToRead];

                await using var stream = File.OpenRead(path);
                stream.Seek(offset, SeekOrigin.Begin);
                var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, (int)bytesToRead), context.CancellationToken);
                yield return new ShellTextLine(System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead));
            }

            yield break;
        }

        if (options.Paths.Count > 0)
        {
            var lines = await TextInputUtilities.ReadLinesFromFilesAsync(options.Paths, context.CancellationToken);

            foreach (var line in lines.TakeLast(options.Count))
            {
                yield return new ShellTextLine(line.Text);
            }

            yield break;
        }

        var items = await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);

        foreach (var item in items.TakeLast(options.Count))
        {
            yield return item;
        }
    }

    private static TailOptions ParseArguments(IReadOnlyList<object?> arguments, string currentDirectory)
    {
        var count = 10;
        long? byteCount = null;
        var follow = false;
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
                case "-f" or "--follow":
                    follow = true;
                    continue;
            }

            if (text.StartsWith("-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unsupported tail option '{text}'.");
            }

            pathArguments.Add(arguments[index]);
        }

        return new TailOptions(count, byteCount, follow, ShellPathArguments.ExpandMany(currentDirectory, pathArguments));
    }

    private sealed record TailOptions(int Count, long? ByteCount, bool Follow, IReadOnlyList<string> Paths);
}
