using System.Text;

namespace Tosh.Runtime;

internal static class RuntimeNamespaceSummaryRenderer
{
    private const int MinOuterWidth = 40;
    private const int MaxOuterWidthFallback = 100;
    private const int InnerMargin = 2; // 1 char of space on each side of inner boxes
    private const int OuterWall = 2;   // "│" on each side of outer box

    // Color + weight scheme — both vary to build a visual hierarchy:
    //   Identifiers ($tosh, TōSh, $tosh.X) → bright-cyan + bold (loudest)
    //   Descriptions (" | Configuration Namespace") → cyan, regular
    //   Top-level boolean True → dim green,  False → dim red (traffic-light)
    //   Top-level values (fallback) → bright-yellow + bold
    //   Footnotes → gray + dim + italic (recedes)
    private static readonly StyledText EmphasisStyle = new(string.Empty, Foreground: "bright-cyan", Bold: true);
    private static readonly StyledText DescriptionStyle = new(string.Empty, Foreground: "cyan");
    private static readonly StyledText TrueStyle = new(string.Empty, Foreground: "green", Dim: true);
    private static readonly StyledText FalseStyle = new(string.Empty, Foreground: "red", Dim: true);
    private static readonly StyledText TopValueFallbackStyle = new(string.Empty, Foreground: "bright-yellow", Bold: true);
    private static readonly StyledText FootnoteStyle = new(string.Empty, Foreground: "gray", Dim: true, Italic: true);

    public static string Render(RuntimeNamespaceDisplaySummary summary)
    {
        var maxOuter = Math.Max(MinOuterWidth, TryGetTerminalWidth() ?? MaxOuterWidthFallback);
        var maxInner = maxOuter - OuterWall - InnerMargin;

        var maxLabelCol = summary.Sections.Count == 0
            ? 0
            : summary.Sections.Max(s => s.Items.Count == 0 ? 0 : s.Items.Max(i => i.Label.Length));

        int SectionNaturalInnerWidth(RuntimeNamespaceSection s)
        {
            var headerText = $"{s.Path} | {s.Description}";
            var headerRowWidth = headerText.Length + 4;
            var widestValue = s.Items.Count == 0 ? 0 : s.Items.Max(i => i.Value.Length);
            var contentRowWidth = maxLabelCol + widestValue + 7;
            return Math.Max(headerRowWidth, contentRowWidth);
        }

        // A mini-box for a top-level scalar looks like:
        //   ╭────────┬───────╮
        //   │ Label  │ Value │   = labelCol + valueCol + 7 chars of box + padding
        //   ╰────────┴───────╯
        // so its inner-width = label + value + 7. Add 4 for outer wrapping.
        int topLevelBoxOuterWidth(IReadOnlyList<(string Label, string Value)> items)
        {
            if (items.Count == 0) return 0;
            return items.Max(i => i.Label.Length + i.Value.Length + 7) + InnerMargin + OuterWall;
        }

        var titleRowWidth = summary.Title.Length + 4;
        var topLevelWidth = topLevelBoxOuterWidth(summary.TopLevelItems);
        var sectionMaxNatural = summary.Sections.Count == 0 ? 0 : summary.Sections.Max(SectionNaturalInnerWidth);
        var footnoteWidth = summary.Footnotes.Count == 0 ? 0 : summary.Footnotes.Max(f => f.Length) + 4;

        var desiredOuterWidth = new[]
        {
            titleRowWidth,
            topLevelWidth,
            sectionMaxNatural + InnerMargin + OuterWall,
            footnoteWidth,
        }.Max();

        var outerWidth = Math.Min(Math.Max(desiredOuterWidth, MinOuterWidth), maxOuter);
        var innerWidth = outerWidth - InnerMargin - OuterWall;
        var outerContentWidth = outerWidth - 2;

        var sb = new StringBuilder();
        sb.AppendLine(BuildTop(outerWidth));
        sb.AppendLine(BuildOuterRowStyled(StyleTitle(summary.Title), summary.Title.Length, outerContentWidth));

        if (summary.TopLevelItems.Count > 0)
        {
            sb.AppendLine(BuildOuterSeparator(outerWidth));
            foreach (var (label, value) in summary.TopLevelItems)
            {
                RenderTopLevelMiniBox(sb, label, value, outerContentWidth);
            }
        }

        for (var i = 0; i < summary.Sections.Count; i++)
        {
            if (i == 0 && summary.TopLevelItems.Count == 0)
            {
                sb.AppendLine(BuildOuterSeparator(outerWidth));
            }
            else if (i > 0 || summary.TopLevelItems.Count > 0)
            {
                sb.AppendLine(BuildOuterRow(string.Empty, outerContentWidth));
            }

            RenderInnerBox(sb, summary.Sections[i], innerWidth, maxLabelCol, outerContentWidth);
        }

        if (summary.Footnotes.Count > 0)
        {
            sb.AppendLine(BuildOuterSeparator(outerWidth));
            var footnoteContentWidth = outerContentWidth - 2;
            foreach (var note in summary.Footnotes)
            {
                foreach (var line in WrapText(note, footnoteContentWidth))
                {
                    var styled = Style(FootnoteStyle, line);
                    sb.AppendLine(BuildOuterRowStyled(styled, line.Length, outerContentWidth));
                }
            }
        }

        sb.Append(BuildBottom(outerWidth));
        return sb.ToString();
    }

    private static void RenderInnerBox(
        StringBuilder sb,
        RuntimeNamespaceSection section,
        int innerWidth,
        int labelCol,
        int outerContentWidth)
    {
        sb.AppendLine(WrapInOuter($"╭{new string('─', innerWidth - 2)}╮", innerWidth, outerContentWidth));

        // Styled inner header: colored "$tosh.X" + " | Description"
        var headerPlain = $"{section.Path} | {section.Description}";
        var headerStyled = StyleSectionHeader(section.Path, section.Description);
        var headerContentWidth = innerWidth - 4;
        var clippedPlain = ClipWithEllipsis(headerPlain, headerContentWidth);
        // If clipping happened, fall back to plain text since segment-level styling is brittle mid-clip.
        var headerBody = clippedPlain == headerPlain ? headerStyled : clippedPlain;
        var headerVisible = clippedPlain.Length;
        var headerPadding = new string(' ', Math.Max(0, headerContentWidth - headerVisible));
        sb.AppendLine(WrapInOuter($"│ {headerBody}{headerPadding} │", innerWidth, outerContentWidth));

        var leftWidth = labelCol + 2;
        var rightWidth = innerWidth - leftWidth - 3;
        sb.AppendLine(WrapInOuter(
            $"├{new string('─', leftWidth)}┬{new string('─', rightWidth)}┤",
            innerWidth,
            outerContentWidth));

        foreach (var (label, value) in section.Items)
        {
            var clippedLabel = ClipWithEllipsis(label, labelCol);
            var clippedValue = ClipWithEllipsis(value, rightWidth - 2);
            var row = $"│ {PadRight(clippedLabel, labelCol)} │ {PadRight(clippedValue, rightWidth - 2)} │";
            sb.AppendLine(WrapInOuter(row, innerWidth, outerContentWidth));
        }

        sb.AppendLine(WrapInOuter(
            $"╰{new string('─', leftWidth)}┴{new string('─', rightWidth)}╯",
            innerWidth,
            outerContentWidth));
    }

    // Renders a standalone 2-column, 1-row mini-table for a top-level scalar,
    // wrapped inside the outer box. Natural width sized to its content.
    //
    //   ╭────────────────────┬───────╮
    //   │ $tosh.IsLoginShell │ False │
    //   ╰────────────────────┴───────╯
    private static void RenderTopLevelMiniBox(
        StringBuilder sb,
        string label,
        string value,
        int outerContentWidth)
    {
        var labelCol = label.Length;
        var valueCol = value.Length;
        var innerWidth = labelCol + valueCol + 7;

        var leftBorder = new string('─', labelCol + 2);
        var rightBorder = new string('─', valueCol + 2);

        sb.AppendLine(WrapInOuter($"╭{leftBorder}┬{rightBorder}╮", innerWidth, outerContentWidth));

        var styledLabel = Style(EmphasisStyle, label);
        var styledValue = Style(StyleForTopLevelValue(value), value);
        sb.AppendLine(WrapInOuter($"│ {styledLabel} │ {styledValue} │", innerWidth, outerContentWidth));

        sb.AppendLine(WrapInOuter($"╰{leftBorder}┴{rightBorder}╯", innerWidth, outerContentWidth));
    }

    private static StyledText StyleForTopLevelValue(string value)
        => value switch
        {
            "True" => TrueStyle,
            "False" => FalseStyle,
            _ => TopValueFallbackStyle,
        };

    // Title pattern: "$tosh | TōSh Live Runtime Namespace"
    // Color "$tosh" and "TōSh" as emphasis; everything else in description color.
    private static string StyleTitle(string title)
    {
        var pipeIndex = title.IndexOf(" | ", StringComparison.Ordinal);
        if (pipeIndex < 0)
        {
            return Style(EmphasisStyle, title);
        }

        var identifier = title[..pipeIndex];            // "$tosh"
        var rest = title[(pipeIndex + 3)..];            // "TōSh Live Runtime Namespace"

        // Within `rest`, try to emphasize the word "TōSh" (or "ToSh") specifically.
        var styledRest = StyleRestWithBrandHighlight(rest);

        return Style(EmphasisStyle, identifier) + Style(DescriptionStyle, " | ") + styledRest;
    }

    private static string StyleRestWithBrandHighlight(string rest)
    {
        // Highlight either "TōSh" or "ToSh" if present at the start as a brand mark.
        foreach (var brand in new[] { "TōSh", "ToSh", "Tosh", "tosh" })
        {
            if (rest.StartsWith(brand + " ", StringComparison.Ordinal) || rest == brand)
            {
                var remainder = rest.Length > brand.Length ? rest[brand.Length..] : string.Empty;
                return Style(EmphasisStyle, brand) + Style(DescriptionStyle, remainder);
            }
        }
        return Style(DescriptionStyle, rest);
    }

    // Section header pattern: "$tosh.Config | Configuration Namespace"
    // Emphasize path ($tosh.Config), description color for " | Configuration Namespace".
    private static string StyleSectionHeader(string path, string description)
    {
        return Style(EmphasisStyle, path) + Style(DescriptionStyle, $" | {description}");
    }

    private static string Style(StyledText template, string text)
    {
        return new StyledText(
            text,
            template.Foreground,
            template.Background,
            template.Bold,
            template.Italic,
            template.Underline,
            template.Dim,
            template.Link).ToAnsi();
    }

    private static string BuildTop(int width)
        => $"╭{new string('─', width - 2)}╮";

    private static string BuildBottom(int width)
        => $"╰{new string('─', width - 2)}╯";

    private static string BuildOuterSeparator(int width)
        => $"├{new string('─', width - 2)}┤";

    private static string BuildOuterRow(string content, int contentWidth)
    {
        var clipped = ClipWithEllipsis(content, contentWidth - 2);
        return $"│ {PadRight(clipped, contentWidth - 2)} │";
    }

    // Render a row where `styledContent` may contain ANSI codes. `plainVisibleLen` is the
    // visible length of the pre-styling text (used for padding calculation).
    private static string BuildOuterRowStyled(string styledContent, int plainVisibleLen, int contentWidth)
    {
        var innerWidth = contentWidth - 2; // for the 1-char padding on each side
        if (plainVisibleLen > innerWidth)
        {
            // Fallback: the styled text is too long; emit plain clipped row.
            var plain = StyledText.StripAnsi(styledContent);
            var clipped = ClipWithEllipsis(plain, innerWidth);
            return $"│ {PadRight(clipped, innerWidth)} │";
        }
        var padding = new string(' ', innerWidth - plainVisibleLen);
        return $"│ {styledContent}{padding} │";
    }

    private static string WrapInOuter(string innerLine, int innerVisibleWidth, int outerContentWidth)
    {
        var targetContent = outerContentWidth - 2;
        var padding = new string(' ', Math.Max(0, targetContent - innerVisibleWidth));
        return $"│ {innerLine}{padding} │";
    }

    private static string PadRight(string text, int width)
    {
        var visible = StyledText.GetVisibleLength(text);
        if (visible >= width) return text;
        return text + new string(' ', width - visible);
    }

    private static string ClipWithEllipsis(string text, int width)
    {
        if (width <= 0) return string.Empty;
        if (text.Length <= width) return text;
        if (width == 1) return "…";
        return text[..(width - 1)] + "…";
    }

    private static IEnumerable<string> WrapText(string text, int width)
    {
        if (width <= 0)
        {
            yield return text;
            yield break;
        }

        var remaining = text;
        while (remaining.Length > width)
        {
            var breakAt = remaining.LastIndexOf(' ', Math.Min(width, remaining.Length - 1));
            if (breakAt <= 0) breakAt = width;
            yield return remaining[..breakAt].TrimEnd();
            remaining = remaining[breakAt..].TrimStart();
        }
        if (remaining.Length > 0) yield return remaining;
    }

    private static int? TryGetTerminalWidth()
    {
        try
        {
            if (Console.IsOutputRedirected) return null;
            return Console.WindowWidth > 1 ? Console.WindowWidth - 1 : null;
        }
        catch
        {
            return null;
        }
    }
}
