using System.Text;
using Tosh.Core;

namespace Tosh.Cli.Tui;

/// <summary>
/// Shared rendering helpers for TUI screens that draw bordered box layouts.
/// </summary>
internal static class TuiRenderHelpers
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
        var remaining = width - StyledText.GetVisibleLength(clipped);

        return remaining <= 0 ? clipped : clipped + new string(' ', remaining);
    }

    public static string ClipPlain(string text, int width)
    {
        if (width <= 0 || string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (StyledText.GetVisibleLength(text) <= width)
        {
            return text;
        }

        if (width == 1)
        {
            return "…";
        }

        var builder = new StringBuilder();

        foreach (var character in text)
        {
            if (StyledText.GetVisibleLength(builder + character.ToString()) >= width)
            {
                break;
            }

            builder.Append(character);
        }

        return builder + "…";
    }
}
