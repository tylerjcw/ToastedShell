using System.Text;

namespace Tosh.Tui;

public static class TextDocumentFormatter
{
    /// <summary>
    /// Wraps text that is already laid out in lines, wrapping each line to the width but
    /// keeping the line breaks the author wrote.
    /// </summary>
    /// <remarks>
    /// <see cref="WrapParagraph"/> splits on all whitespace, newlines included, which is
    /// right for a paragraph and wrong for anything shaped: a block with headings, columns
    /// or blank lines comes back as one run-on paragraph. Use this where the newlines are
    /// content rather than accident.
    /// </remarks>
    public static IReadOnlyList<string> WrapDocument(string text, int width)
    {
        ArgumentNullException.ThrowIfNull(text);
        width = Math.Max(1, width);

        var lines = new List<string>();

        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Length == 0)
            {
                // A blank line is a deliberate gap; wrapping it would swallow it.
                lines.Add(string.Empty);
                continue;
            }

            lines.AddRange(WrapParagraph(line, width));
        }

        return lines;
    }

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
