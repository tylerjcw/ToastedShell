namespace Tosh.Core.Commands;

public sealed class TranslateCommand : ShellCommand
{
    public TranslateCommand()
        : base("tr", "Translates or deletes characters in text.", "tr [-d] <set1> [set2] [path ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var options = ParseOptions(context.Arguments, context.Runtime.CurrentDirectory);
        var lines = options.Paths.Count > 0
            ? await TextInputUtilities.ReadLinesFromFilesAsync(options.Paths, context.CancellationToken)
            : await TextInputUtilities.ReadLinesFromInputAsync(context, "tr expects text input or file paths.");

        foreach (var line in lines)
        {
            yield return new ShellTextLine(Translate(line.Text, options));
        }
    }

    private static string Translate(string text, TrOptions options)
    {
        var source = ExpandSet(options.Set1);
        var target = options.Delete ? Array.Empty<char>() : ExpandSet(options.Set2 ?? string.Empty);
        var builder = new System.Text.StringBuilder(text.Length);

        foreach (var character in text)
        {
            var sourceIndex = source.IndexOf(character);

            if (sourceIndex < 0)
            {
                builder.Append(character);
                continue;
            }

            if (options.Delete)
            {
                continue;
            }

            if (target.Length == 0)
            {
                continue;
            }

            var mappedIndex = Math.Min(sourceIndex, target.Length - 1);
            builder.Append(target[mappedIndex]);
        }

        return builder.ToString();
    }

    private static char[] ExpandSet(string text)
    {
        var values = new List<char>();

        for (var index = 0; index < text.Length; index++)
        {
            if (index + 2 < text.Length &&
                text[index + 1] == '-' &&
                text[index] <= text[index + 2])
            {
                for (var character = text[index]; character <= text[index + 2]; character++)
                {
                    values.Add(character);
                }

                index += 2;
                continue;
            }

            values.Add(text[index]);
        }

        return values.ToArray();
    }

    private static TrOptions ParseOptions(IReadOnlyList<object?> arguments, string currentDirectory)
    {
        var delete = false;
        string? set1 = null;
        string? set2 = null;
        var pathArguments = new List<object?>();

        for (var index = 0; index < arguments.Count; index++)
        {
            var text = arguments[index]?.ToString();

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (text == "-d")
            {
                delete = true;
                continue;
            }

            if (set1 is null)
            {
                set1 = text;
                continue;
            }

            if (!delete && set2 is null)
            {
                set2 = text;
                continue;
            }

            if (text.StartsWith("-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unsupported tr option '{text}'.");
            }

            pathArguments.Add(arguments[index]);
        }

        if (string.IsNullOrWhiteSpace(set1))
        {
            throw new InvalidOperationException("tr requires at least one set argument.");
        }

        if (!delete && set2 is null)
        {
            throw new InvalidOperationException("tr requires a replacement set unless -d is used.");
        }

        return new TrOptions(delete, set1, set2, ShellPathArguments.ExpandMany(currentDirectory, pathArguments));
    }

    private sealed record TrOptions(bool Delete, string Set1, string? Set2, IReadOnlyList<string> Paths);
}
