using System.Text;

namespace Tosh.Core.Commands;

public sealed class WordCountCommand : ShellCommand
{
    public WordCountCommand()
        : base("wc", "Counts lines, words, bytes, and characters.", "wc [path ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count > 0)
        {
            var paths = context.Arguments
                .Select(argument => PathUtilities.ResolvePath(context.Runtime.CurrentDirectory, CommandArguments.RequireString([argument], 0, "path")))
                .ToArray();

            foreach (var path in paths)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                if (!File.Exists(path))
                {
                    throw new InvalidOperationException($"File '{path}' does not exist.");
                }

                var fileText = await File.ReadAllTextAsync(path, context.CancellationToken);
                yield return CreateStatistics(path, fileText, new FileInfo(path).Length);
            }

            yield break;
        }

        var values = await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);

        if (values.Count == 0)
        {
            throw new InvalidOperationException("wc expects text input or file paths.");
        }

        var text = string.Join(Environment.NewLine, values.Select(ExternalTextSerializer.Serialize));
        var bytes = Encoding.UTF8.GetByteCount(text);
        yield return CreateStatistics(null, text, bytes);
    }

    private static TextStatistics CreateStatistics(string? path, string text, long bytes)
    {
        var lines = TextInputUtilities.SplitLines(text).Count();
        return new TextStatistics(path, lines, TextInputUtilities.CountWords(text), bytes, text.Length);
    }
}
