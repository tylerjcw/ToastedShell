using System.Text;

namespace Tosh.Runtime;

internal static class TextInputUtilities
{
    public static async Task<IReadOnlyList<string>> ReadScalarValuesFromInputAsync(
        CommandContext context,
        string? missingInputMessage = null,
        bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(context);

        var inputValues = await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);

        if (inputValues.Count == 0)
        {
            if (allowEmpty)
            {
                return Array.Empty<string>();
            }

            throw new InvalidOperationException(missingInputMessage ?? "This command expects scalar input.");
        }

        return inputValues
            .Select(ExternalTextSerializer.Serialize)
            .ToArray();
    }

    public static async Task<IReadOnlyList<TextInputLine>> ReadLinesFromInputAsync(
        CommandContext context,
        string? missingInputMessage = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        var inputValues = await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);

        if (inputValues.Count == 0)
        {
            throw new InvalidOperationException(missingInputMessage ?? "This command expects text input.");
        }

        var lines = new List<TextInputLine>();
        var lineNumber = 1;

        foreach (var value in inputValues)
        {
            AddLines(lines, ExternalTextSerializer.Serialize(value), null, ref lineNumber);
        }

        return lines;
    }

    public static async Task<IReadOnlyList<TextInputLine>> ReadLinesFromFilesAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var lines = new List<TextInputLine>();

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"File '{path}' does not exist.");
            }

            await using var stream = File.OpenRead(path);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var lineNumber = 1;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(cancellationToken);

                if (line is null)
                {
                    break;
                }

                lines.Add(new TextInputLine(line, path, lineNumber));
                lineNumber++;
            }
        }

        return lines;
    }

    public static IEnumerable<string> SplitLines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        using var reader = new StringReader(text);

        while (true)
        {
            var line = reader.ReadLine();

            if (line is null)
            {
                yield break;
            }

            yield return line;
        }
    }

    public static int CountWords(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return text
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Length;
    }

    private static void AddLines(List<TextInputLine> lines, string text, string? path, ref int lineNumber)
    {
        var emitted = false;

        foreach (var line in SplitLines(text))
        {
            lines.Add(new TextInputLine(line, path, lineNumber));
            lineNumber++;
            emitted = true;
        }

        if (!emitted && text.Length == 0)
        {
            lines.Add(new TextInputLine(string.Empty, path, lineNumber));
            lineNumber++;
        }
    }
}
