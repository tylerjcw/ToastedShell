namespace Tosh.Cli.Tui;

internal enum TuiFormRowKind
{
    Body,
    Meta,
    Preview,
}

internal sealed record TuiFormRow(
    string Label,
    string? Value = null,
    TuiFormRowKind Kind = TuiFormRowKind.Body,
    bool IsSelected = false);

internal readonly record struct TuiFormEntry(string Text, TuiFormRowKind Kind);

internal static class TuiFormLayout
{
    public static IReadOnlyList<TuiFormEntry> BuildEntries(
        IReadOnlyList<TuiFormRow> rows,
        int width,
        int labelWidth = 18,
        int gap = 2)
    {
        ArgumentNullException.ThrowIfNull(rows);

        if (rows.Count == 0)
        {
            return Array.Empty<TuiFormEntry>();
        }

        var normalizedWidth = Math.Max(20, width);
        var normalizedLabelWidth = Math.Clamp(labelWidth, 8, Math.Max(8, normalizedWidth - 8));
        var prefixWidth = 2;
        var valueWidth = Math.Max(8, normalizedWidth - prefixWidth - normalizedLabelWidth - gap);
        var entries = new List<TuiFormEntry>(rows.Count * 2);

        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(row.Label) && string.IsNullOrEmpty(row.Value))
            {
                entries.Add(new TuiFormEntry(string.Empty, row.Kind));
                continue;
            }

            var prefix = row.IsSelected ? "> " : "  ";

            if (string.IsNullOrWhiteSpace(row.Value))
            {
                entries.AddRange(TextDocumentFormatter.WrapParagraph(prefix + row.Label, normalizedWidth)
                    .Select(text => new TuiFormEntry(text, row.Kind)));
                continue;
            }

            var clippedLabel = ClipPlain(row.Label, normalizedLabelWidth);
            var paddedLabel = clippedLabel + new string(' ', Math.Max(0, normalizedLabelWidth - clippedLabel.Length));
            var wrappedValue = TextDocumentFormatter.WrapParagraph(row.Value, valueWidth).ToArray();

            if (wrappedValue.Length == 0)
            {
                entries.Add(new TuiFormEntry(prefix + paddedLabel, row.Kind));
                continue;
            }

            entries.Add(new TuiFormEntry(prefix + paddedLabel + new string(' ', gap) + wrappedValue[0], row.Kind));

            if (wrappedValue.Length == 1)
            {
                continue;
            }

            var continuationIndent = new string(' ', prefixWidth + normalizedLabelWidth + gap);

            for (var index = 1; index < wrappedValue.Length; index++)
            {
                entries.Add(new TuiFormEntry(continuationIndent + wrappedValue[index], row.Kind));
            }
        }

        return entries;
    }

    private static string ClipPlain(string text, int width)
    {
        if (width <= 0 || string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (text.Length <= width)
        {
            return text;
        }

        if (width == 1)
        {
            return "…";
        }

        return text[..Math.Max(0, width - 1)] + "…";
    }
}
