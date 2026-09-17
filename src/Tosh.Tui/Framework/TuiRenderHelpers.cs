using System.Globalization;
using System.Text;
using Tosh.Tui.Rendering;
using Tosh.Runtime;

namespace Tosh.Tui;

/// <summary>
/// Shared rendering helpers for TUI screens that draw bordered box layouts.
/// </summary>
public static class TuiRenderHelpers
{
    public static string RenderTopBorder(int width, string title, ToshTuiThemeConfig theme, TuiBoxCharacters box)
    {
        if (width <= 1)
        {
            return string.Empty;
        }

        var innerWidth = Math.Max(0, width - 2);
        var clippedTitle = ClipPlain(title, Math.Max(0, innerWidth - 2));
        var titleText = string.IsNullOrWhiteSpace(clippedTitle) ? string.Empty : $" {clippedTitle} ";
        var fillWidth = Math.Max(0, innerWidth - titleText.Length);

        return StyledText.RenderSegments(
        [
            theme.Border.Apply(box.TopLeft.ToString()),
            theme.Title.Apply(titleText),
            theme.Border.Apply(new string(box.Horizontal, fillWidth) + box.TopRight),
        ]);
    }

    public static string RenderBottomBorder(int width, ToshTuiThemeConfig theme, TuiBoxCharacters box)
    {
        if (width <= 1)
        {
            return string.Empty;
        }

        return theme.Border.Apply($"{box.BottomLeft}{new string(box.Horizontal, Math.Max(0, width - 2))}{box.BottomRight}").ToAnsi();
    }

    public static string RenderBoxContentLine(
        string plainText,
        int width,
        ToshTextStyleConfig contentStyle,
        ToshTuiThemeConfig theme,
        TuiBoxCharacters box)
    {
        if (width <= 1)
        {
            return string.Empty;
        }

        var innerWidth = Math.Max(1, width - 2);
        var padded = TrimOrPadPlain(plainText, innerWidth);

        return StyledText.RenderSegments(
        [
            theme.Border.Apply(box.Vertical.ToString()),
            contentStyle.Apply(padded),
            theme.Border.Apply(box.Vertical.ToString()),
        ]);
    }

    /// <summary>
    /// Renders a box line containing multiple styled segments (e.g. gutter + content).
    /// Clips/pads the combined segments to fit the inner width, with borders on each side.
    /// </summary>
    public static string RenderStyledBoxLine(
        IEnumerable<(string Text, ToshTextStyleConfig Style)> segments,
        int width,
        ToshTuiThemeConfig theme,
        TuiBoxCharacters box)
    {
        if (width <= 1)
        {
            return string.Empty;
        }

        var innerWidth = Math.Max(1, width - 2);
        var renderedInner = RenderStyledSegments(segments, innerWidth);

        return StyledText.RenderSegments(
        [
            theme.Border.Apply(box.Vertical.ToString()),
            renderedInner,
            theme.Border.Apply(box.Vertical.ToString()),
        ]);
    }

    /// <summary>
    /// Renders a sequence of styled (text, style) segments, clipping to the given width and padding with spaces.
    /// </summary>
    public static string RenderStyledSegments(IEnumerable<(string Text, ToshTextStyleConfig Style)> segments, int width)
    {
        var builder = new StringBuilder();
        var remaining = width;

        foreach (var (text, style) in segments)
        {
            if (remaining <= 0)
            {
                break;
            }

            var clipped = text.Length <= remaining ? text : text[..remaining];
            builder.Append(style.Apply(clipped).ToAnsi());
            remaining -= clipped.Length;
        }

        if (remaining > 0)
        {
            builder.Append(' ', remaining);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Renders a full-width footer line: text is trimmed/padded to width and styled with the footer theme.
    /// </summary>
    public static string RenderFooterLine(string text, int width, ToshTuiThemeConfig theme)
    {
        return theme.Footer.Apply(TrimOrPadPlain(text, width)).ToAnsi();
    }

    public static ToshTextStyleConfig MergeListStyles(
        ToshTextStyleConfig baseStyle,
        ToshTextStyleConfig selectedStyle,
        bool isSelected,
        bool preserveForeground)
    {
        if (!isSelected)
        {
            return baseStyle;
        }

        return new ToshTextStyleConfig(
            foreground: preserveForeground ? baseStyle.Foreground : selectedStyle.Foreground ?? baseStyle.Foreground,
            background: selectedStyle.Background ?? baseStyle.Background,
            bold: baseStyle.Bold || selectedStyle.Bold,
            italic: baseStyle.Italic || selectedStyle.Italic,
            underline: baseStyle.Underline || selectedStyle.Underline,
            dim: selectedStyle.Dim && baseStyle.Dim);
    }

    public static string FormatBoolean(bool value) => value ? "yes" : "no";

    /// <summary>
    /// Renders a single search row: "│label: query          │" with styled label, query and padding.
    /// </summary>
    public static string RenderSearchRow(
        string label,
        string query,
        int width,
        ToshTuiThemeConfig theme,
        TuiBoxCharacters box)
    {
        var innerWidth = Math.Max(1, width - 2);
        var labelText = $"{label}: ";
        var queryWidth = Math.Max(0, innerWidth - labelText.Length);
        var clippedQuery = ClipPlain(query, queryWidth);
        var labelStyled = theme.SearchLabel.Apply(labelText).ToAnsi();
        var queryStyled = theme.SearchInput.Apply(clippedQuery).ToAnsi();
        var visibleLength = StyledText.GetVisibleLength(labelStyled) + StyledText.GetVisibleLength(queryStyled);
        var padding = new string(' ', Math.Max(0, innerWidth - visibleLength));

        var sb = new StringBuilder();
        sb.Append(theme.Border.Apply(box.Vertical.ToString()).ToAnsi());
        sb.Append(labelStyled);
        sb.Append(queryStyled);
        sb.Append(padding);
        sb.Append(theme.Border.Apply(box.Vertical.ToString()).ToAnsi());
        return sb.ToString();
    }

    /// <summary>
    /// Renders a dual-pane content area: left/right titled columns with bordered rows.
    /// <paramref name="renderLeftLine"/> receives (itemIndex, isSelected) and returns the full rendered line.
    /// <paramref name="renderRightLine"/> receives (entryIndex) and returns the full rendered line.
    /// </summary>
    public static string RenderDualPaneContent(
        TuiRect leftRect,
        TuiRect rightRect,
        string leftTitle,
        string rightTitle,
        (int Start, int Length) leftVisibleRange,
        (int Start, int Length) rightVisibleRange,
        int leftSelectedIndex,
        Func<int, bool, string> renderLeftLine,
        Func<int, string> renderRightLine,
        ToshTuiThemeConfig theme,
        TuiBoxCharacters box)
    {
        var builder = new StringBuilder();
        var leftContentRows = Math.Max(1, leftRect.Height - 2);
        var rightContentRows = Math.Max(1, rightRect.Height - 2);

        builder.Append(RenderTopBorder(leftRect.Width, leftTitle, theme, box));
        builder.Append(' ');
        builder.Append(RenderTopBorder(rightRect.Width, rightTitle, theme, box));
        builder.AppendLine();

        for (var row = 0; row < Math.Max(leftContentRows, rightContentRows); row++)
        {
            string leftLine;
            if (row < leftContentRows && row < leftVisibleRange.Length)
            {
                var itemIndex = leftVisibleRange.Start + row;
                leftLine = renderLeftLine(itemIndex, itemIndex == leftSelectedIndex);
            }
            else
            {
                leftLine = RenderBoxContentLine(string.Empty, leftRect.Width, theme.ListItem, theme, box);
            }

            string rightLine;
            if (row < rightContentRows && row < rightVisibleRange.Length)
            {
                rightLine = renderRightLine(rightVisibleRange.Start + row);
            }
            else
            {
                rightLine = RenderBoxContentLine(string.Empty, rightRect.Width, theme.DetailText, theme, box);
            }

            builder.Append(leftLine);
            builder.Append(' ');
            builder.Append(rightLine);
            builder.AppendLine();
        }

        builder.Append(RenderBottomBorder(leftRect.Width, theme, box));
        builder.Append(' ');
        builder.Append(RenderBottomBorder(rightRect.Width, theme, box));
        builder.AppendLine();

        return builder.ToString();
    }

    public static string TrimOrPadPlain(string text, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        var clipped = ClipPlain(text, width);

        // Measured in columns, like the clip that produced it. Padding by the code-unit
        // length put three spaces after a two-character CJK label that already occupied
        // four columns, and pushed the box's right-hand border two columns out on exactly
        // the rows that had non-ASCII text in them (`TUI-0005`).
        var remaining = width - VisibleColumns(clipped);

        return remaining <= 0 ? clipped : clipped + new string(' ', remaining);
    }

    /// <summary>
    /// Clips text to a width, marking the cut.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to append one character at a time and recompute the visible length of the
    /// whole accumulated string on each pass, which made it quadratic in the width it was
    /// clipping to — and it is called once per line per frame, so a 200-column screen paid
    /// for it forty times over (<c>TUI-0012</c>).
    /// </para>
    /// <para>
    /// It also measured in UTF-16 code units, so a line with an emoji or a CJK character in
    /// it was clipped to the wrong place (<c>TUI-0005</c>). Both go away by deferring to
    /// <see cref="TuiTextMeasure"/>, which counts columns and walks the text once.
    /// </para>
    /// </remarks>
    public static string ClipPlain(string text, int width)
    {
        if (width <= 0 || string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // The overwhelmingly common case, and the one the measure already answers.
        return text.Contains('\x1b', StringComparison.Ordinal)
            ? ClipStyled(text, width)
            : TuiTextMeasure.Elide(text, width);
    }

    /// <summary>
    /// The same, for text that carries its own colour.
    /// </summary>
    /// <remarks>
    /// Escape sequences are copied through and cost no columns, so a clipped line keeps the
    /// styling of the part that survived. Highlighted source in the config browser's preview
    /// pane arrives here, which is why this path exists at all.
    /// </remarks>
    private static string ClipStyled(string text, int width)
    {
        if (VisibleColumns(text) <= width)
        {
            return text;
        }

        if (width == 1)
        {
            return "…";
        }

        var builder = new StringBuilder();
        var used = 0;
        var index = 0;

        while (index < text.Length)
        {
            if (text[index] == '\x1b')
            {
                var end = EscapeEnd(text, index);

                builder.Append(text, index, end - index);
                index = end;
                continue;
            }

            var length = StringInfo.GetNextTextElementLength(text.AsSpan(index));
            var cluster = text.Substring(index, length);
            var columns = TuiTextMeasure.ClusterWidth(cluster);

            // One column is kept back for the mark.
            if (used + columns > width - 1)
            {
                break;
            }

            builder.Append(cluster);
            used += columns;
            index += length;
        }

        return builder.Append('…').ToString();
    }

    /// <summary>How many columns text occupies once its styling is discounted.</summary>
    /// <remarks>
    /// Stripping allocates, so it is done only when there is something to strip — which is
    /// nearly never. This is called once per line per frame, so "nearly never" is worth the
    /// branch (<c>TUI-0012</c>).
    /// </remarks>
    private static int VisibleColumns(string text)
        => TuiTextMeasure.MeasureWidth(
            text.Contains('\x1b', StringComparison.Ordinal) ? StyledText.StripAnsi(text) : text);

    /// <summary>Where the escape sequence starting at <paramref name="start"/> ends.</summary>
    /// <remarks>
    /// CSI — <c>ESC [</c> — runs to a byte in the 0x40-0x7E range, which is the <c>m</c> of
    /// an SGR introducer. Anything else is taken as two characters, which is enough to keep
    /// the walk moving past a sequence this does not know.
    /// </remarks>
    private static int EscapeEnd(string text, int start)
    {
        if (start + 1 >= text.Length || text[start + 1] != '[')
        {
            return Math.Min(text.Length, start + 2);
        }

        var index = start + 2;

        while (index < text.Length && text[index] is < '\x40' or > '\x7e')
        {
            index += 1;
        }

        return Math.Min(text.Length, index + 1);
    }
}
