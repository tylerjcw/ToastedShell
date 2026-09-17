using System.Text;

using Tosh.Tui.Rendering;

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

        // A single line that already fits is returned exactly as it was written.
        //
        // Wrapping splits on whitespace and rejoins with single spaces, which is what
        // wrapping *means* — and it was doing it to lines that never needed wrapping, so a
        // block of aligned columns came back reflowed. `examples/system-monitor.tosh` drew
        // `Used 42.8 GB` where its source says `  Used       42.8 GB`, and this type's own
        // summary says a block with headings and columns is not a paragraph.
        //
        // Text carrying its own line breaks or tabs does not take this path. Collapsing
        // those is the documented contract — a paragraph's newlines are an artefact of how
        // it was typed — and a tab has no width a cell grid can agree on.
        //
        // Measured in columns rather than code units, like everything else that decides
        // whether text fits (`TUI-0005`).
        if (text.AsSpan().IndexOfAny('\n', '\r', '\t') < 0 &&
            TuiTextMeasure.MeasureWidth(indent) + TuiTextMeasure.MeasureWidth(text) <= width)
        {
            return [indent + text];
        }

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

            if (TuiTextMeasure.MeasureWidth(candidate) <= width ||
                current.Length == currentIndent.Length)
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
