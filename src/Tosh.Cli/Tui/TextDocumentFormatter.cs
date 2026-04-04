using System.Text;

namespace Tosh.Cli.Tui;

internal static class TextDocumentFormatter
{
    public static IReadOnlyList<string> WrapParagraph(string text, int width, string indent = "", string subsequentIndent = "")
    {
        ArgumentNullException.ThrowIfNull(text);
        width = Math.Max(1, width);

        var words = text
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        if (words.Length == 0)
        {
            return [indent];
        }

        var lines = new List<string>();
        var current = new StringBuilder(indent);
        var currentIndent = indent;

        foreach (var word in words)
        {
            var candidate = current.Length == currentIndent.Length
                ? currentIndent + word
                : current + " " + word;

            if (candidate.Length <= width || current.Length == currentIndent.Length)
            {
                if (current.Length > currentIndent.Length)
                {
                    current.Append(' ');
                }

                current.Append(word);
                continue;
            }

            lines.Add(current.ToString());
            current.Clear();
            currentIndent = subsequentIndent;
            current.Append(subsequentIndent);
            current.Append(word);
        }

        lines.Add(current.ToString());
        return lines;
    }
}
