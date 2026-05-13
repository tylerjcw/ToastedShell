using System.Text;

namespace Tosh.Runtime;

// Single-record renderer for `Tosh.Runtime.HelpTopic`. Mirrors the layout
// pattern of `RuntimeNamespaceSummaryRenderer` (outer rounded box with
// nested sub-boxes), but tailored to a help page: title bar, description,
// and conditional sections for Usage / Arguments / Options / Pipeline /
// Examples / Aliases / Related, plus a footer for Notes and a navigation
// hint.
internal static class HelpTopicSummaryRenderer
{
    private const int MinOuterWidth = 50;
    private const int MaxOuterWidthFallback = 150;
    private const int PreferredOuterWidth = 150; // soft floor when the terminal is wide enough — gives the description column room to breathe
    private const int InnerMargin = 2; // 1 char of space on each side of inner boxes
    private const int OuterWall = 2;   // "│" on each side of outer box

    // Color palette — coordinated with RuntimeNamespaceSummaryRenderer so
    // help pages and `$tosh` feel like they belong to the same shell.
    private static readonly StyledText TitleNameStyle = new(string.Empty, Foreground: "bright-cyan", Bold: true);
    private static readonly StyledText TitleSeparatorStyle = new(string.Empty, Foreground: "gray", Dim: true);
    private static readonly StyledText TitleKindStyle = new(string.Empty, Foreground: "cyan");
    private static readonly StyledText TitleCategoryStyle = new(string.Empty, Foreground: "cyan", Dim: true);
    private static readonly StyledText DescriptionStyle = new(string.Empty, Italic: true);
    private static readonly StyledText SectionHeaderStyle = new(string.Empty, Foreground: "bright-yellow", Bold: true, Underline: true);
    private static readonly StyledText FlagStyle = new(string.Empty, Foreground: "bright-cyan");
    private static readonly StyledText PlaceholderStyle = new(string.Empty, Foreground: "yellow", Italic: true, Dim: true);
    private static readonly StyledText ChoiceStyle = new(string.Empty, Foreground: "magenta");
    private static readonly StyledText DimStyle = new(string.Empty, Foreground: "gray", Dim: true);
    private static readonly StyledText RequiredStyle = new(string.Empty, Foreground: "green", Bold: true);
    private static readonly StyledText OptionalMarkStyle = new(string.Empty, Foreground: "gray", Dim: true);
    private static readonly StyledText TypeStyle = new(string.Empty, Foreground: "green");
    private static readonly StyledText ExampleBulletStyle = new(string.Empty, Foreground: "bright-green", Bold: true);
    private static readonly StyledText ExampleDescStyle = new(string.Empty, Foreground: "gray", Dim: true, Italic: true);
    private static readonly StyledText RelatedDotStyle = new(string.Empty, Foreground: "gray", Dim: true);
    private static readonly StyledText RelatedItemStyle = new(string.Empty, Foreground: "cyan");
    private static readonly StyledText FootnoteStyle = new(string.Empty, Foreground: "gray", Dim: true, Italic: true);
    private static readonly StyledText NavHintStyle = new(string.Empty, Foreground: "gray", Dim: true);
    private static readonly StyledText PathStyle = new(string.Empty, Foreground: "bright-blue");

    public static string Render(HelpTopic topic, Func<string, string>? codeHighlighter = null)
    {
        var maxOuter = Math.Max(MinOuterWidth, TryGetTerminalWidth() ?? MaxOuterWidthFallback);

        // Decide outer width by looking at content extents.
        var titlePlain = BuildTitlePlain(topic);
        var navHintPlain = "Use 'help <name>' to drill in, or 'help " + topic.Name + " | to json' for raw data.";

        var desired = new[]
        {
            titlePlain.Length + 4,
            navHintPlain.Length + 4,
            (topic.Description?.Length ?? 0) + 4,
            ComputeUsageDesiredOuter(topic.Usage),
            ComputeArgumentsDesiredOuter(topic.Arguments),
            ComputeOptionsDesiredOuter(topic.Options),
            ComputePipelineDesiredOuter(topic.PipelineInput, topic.Output),
            ComputeRelatedDesiredOuter(topic.Related),
            ComputePathDesiredOuter(topic.Path),
            ComputeAliasesDesiredOuter(topic.Aliases),
            // Examples are aspirational; they wrap, so don't blow up width on them.
            64,
        }.Max();

        // Prefer a generous default width when the terminal allows so descriptions wrap less.
        var preferred = Math.Min(maxOuter, PreferredOuterWidth);
        var outerWidth = Math.Min(Math.Max(Math.Max(desired, preferred), MinOuterWidth), maxOuter);
        var innerWidth = outerWidth - InnerMargin - OuterWall;
        var outerContentWidth = outerWidth - 2;

        var sb = new StringBuilder();
        sb.AppendLine(BuildTop(outerWidth));

        // ── Title bar ────────────────────────────────────────────────────────
        var (titleStyled, titleVisible) = StyleTitle(topic);
        sb.AppendLine(BuildOuterRowStyled(titleStyled, titleVisible, outerContentWidth));

        sb.AppendLine(BuildOuterSeparator(outerWidth));

        // ── Description ──────────────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(topic.Description))
        {
            foreach (var line in WrapText(topic.Description, outerContentWidth - 2))
            {
                var styled = Style(DescriptionStyle, line);
                sb.AppendLine(BuildOuterRowStyled(styled, line.Length, outerContentWidth));
            }
        }

        // ── Path (for user-defined functions/scripts) ───────────────────────
        if (!string.IsNullOrWhiteSpace(topic.Path))
        {
            sb.AppendLine(BuildOuterRow(string.Empty, outerContentWidth));
            var prefix = "Source: ";
            var pathLine = prefix + topic.Path;
            foreach (var line in WrapText(pathLine, outerContentWidth - 2))
            {
                if (line.StartsWith(prefix))
                {
                    var combined = Style(DimStyle, prefix) + Style(PathStyle, line[prefix.Length..]);
                    sb.AppendLine(BuildOuterRowStyled(combined, line.Length, outerContentWidth));
                }
                else
                {
                    sb.AppendLine(BuildOuterRowStyled(Style(PathStyle, line), line.Length, outerContentWidth));
                }
            }
        }

        var firstSubBox = true;

        void EmitSubBoxSpacer()
        {
            sb.AppendLine(BuildOuterRow(string.Empty, outerContentWidth));
            firstSubBox = false;
        }

        // ── Aliases (compact, shown above sub-boxes if present) ─────────────
        if (topic.Aliases is { Count: > 0 })
        {
            sb.AppendLine(BuildOuterRow(string.Empty, outerContentWidth));
            var label = "Aliases: ";
            var values = string.Join("  ", topic.Aliases);
            var line = label + values;
            foreach (var wrapped in WrapText(line, outerContentWidth - 2))
            {
                if (wrapped.StartsWith(label))
                {
                    var combined = Style(DimStyle, label) + Style(FlagStyle, wrapped[label.Length..]);
                    sb.AppendLine(BuildOuterRowStyled(combined, wrapped.Length, outerContentWidth));
                }
                else
                {
                    sb.AppendLine(BuildOuterRowStyled(Style(FlagStyle, wrapped), wrapped.Length, outerContentWidth));
                }
            }
            firstSubBox = false;
        }

        // ── Usage sub-box ────────────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(topic.Usage))
        {
            if (!firstSubBox) EmitSubBoxSpacer(); else { sb.AppendLine(BuildOuterRow(string.Empty, outerContentWidth)); firstSubBox = false; }
            RenderUsageSubBox(sb, topic.Usage, innerWidth, outerContentWidth);
        }

        // ── Arguments sub-box ────────────────────────────────────────────────
        if (topic.Arguments is { Count: > 0 } args)
        {
            if (!firstSubBox) EmitSubBoxSpacer(); else { sb.AppendLine(BuildOuterRow(string.Empty, outerContentWidth)); firstSubBox = false; }
            RenderArgumentsSubBox(sb, args, innerWidth, outerContentWidth);
        }

        // ── Options sub-box ──────────────────────────────────────────────────
        if (topic.Options is { Count: > 0 } opts)
        {
            if (!firstSubBox) EmitSubBoxSpacer(); else { sb.AppendLine(BuildOuterRow(string.Empty, outerContentWidth)); firstSubBox = false; }
            RenderOptionsSubBox(sb, opts, innerWidth, outerContentWidth);
        }

        // ── Pipeline sub-box (Accepts / Output) ──────────────────────────────
        if (topic.PipelineInput is not null || !string.IsNullOrWhiteSpace(topic.Output) || !string.IsNullOrWhiteSpace(topic.Streaming))
        {
            if (!firstSubBox) EmitSubBoxSpacer(); else { sb.AppendLine(BuildOuterRow(string.Empty, outerContentWidth)); firstSubBox = false; }
            RenderPipelineSubBox(sb, topic.PipelineInput, topic.Output, topic.Streaming, innerWidth, outerContentWidth);
        }

        // ── Examples sub-box ─────────────────────────────────────────────────
        var hasStructured = topic.ExampleItems is { Count: > 0 };
        var hasPlain = topic.Examples is { Count: > 0 };
        if (hasStructured || hasPlain)
        {
            if (!firstSubBox) EmitSubBoxSpacer(); else { sb.AppendLine(BuildOuterRow(string.Empty, outerContentWidth)); firstSubBox = false; }
            RenderExamplesSubBox(sb, topic, codeHighlighter, innerWidth, outerContentWidth);
        }

        // ── Related sub-box ──────────────────────────────────────────────────
        if (topic.Related is { Count: > 0 } related)
        {
            if (!firstSubBox) EmitSubBoxSpacer(); else { sb.AppendLine(BuildOuterRow(string.Empty, outerContentWidth)); firstSubBox = false; }
            RenderRelatedSubBox(sb, related, innerWidth, outerContentWidth);
        }

        // ── Footer: Notes + nav hint ─────────────────────────────────────────
        var hasFooterContent = !string.IsNullOrWhiteSpace(topic.Notes);
        sb.AppendLine(BuildOuterSeparator(outerWidth));

        if (hasFooterContent)
        {
            foreach (var line in WrapText(topic.Notes!, outerContentWidth - 2))
            {
                var styled = Style(FootnoteStyle, line);
                sb.AppendLine(BuildOuterRowStyled(styled, line.Length, outerContentWidth));
            }
        }

        foreach (var line in WrapText(navHintPlain, outerContentWidth - 2))
        {
            var styled = Style(NavHintStyle, line);
            sb.AppendLine(BuildOuterRowStyled(styled, line.Length, outerContentWidth));
        }

        sb.Append(BuildBottom(outerWidth));
        return sb.ToString();
    }

    // ─── Section renderers ───────────────────────────────────────────────────

    private static void RenderUsageSubBox(StringBuilder sb, string usage, int innerWidth, int outerContentWidth)
    {
        var headerContentWidth = innerWidth - 4;
        sb.AppendLine(WrapInOuter($"╭{new string('─', innerWidth - 2)}╮", innerWidth, outerContentWidth));
        sb.AppendLine(WrapInOuter(BuildSubBoxHeader("Usage", innerWidth), innerWidth, outerContentWidth));
        sb.AppendLine(WrapInOuter($"├{new string('─', innerWidth - 2)}┤", innerWidth, outerContentWidth));

        foreach (var line in WrapText(usage, headerContentWidth))
        {
            var styled = StyleUsageLine(line);
            sb.AppendLine(WrapInOuter($"│ {styled}{Pad(headerContentWidth - line.Length)} │", innerWidth, outerContentWidth));
        }

        sb.AppendLine(WrapInOuter($"╰{new string('─', innerWidth - 2)}╯", innerWidth, outerContentWidth));
    }

    private static void RenderArgumentsSubBox(
        StringBuilder sb,
        IReadOnlyList<HelpArgumentInfo> args,
        int innerWidth,
        int outerContentWidth)
    {
        var contentWidth = innerWidth - 4; // for "│ ... │"
        var nameCol = Math.Max(4, args.Max(a => a.Name.Length));
        var reqCol = 3;
        var typeCol = Math.Max(4, args.Max(a => (a.TypeName ?? "").Length));
        // Cap typeCol so description has room.
        typeCol = Math.Min(typeCol, Math.Max(4, contentWidth / 4));
        // Three two-space separators between four columns = 6 chars of spacing.
        var descCol = contentWidth - nameCol - reqCol - typeCol - 6;
        if (descCol < 12)
        {
            // Drop type column when very narrow.
            typeCol = 0;
            descCol = contentWidth - nameCol - reqCol - 4; // two separators
        }
        if (descCol < 8)
        {
            // Last resort: drop req column too.
            reqCol = 0;
            descCol = contentWidth - nameCol - 2; // one separator
        }

        sb.AppendLine(WrapInOuter($"╭{new string('─', innerWidth - 2)}╮", innerWidth, outerContentWidth));
        sb.AppendLine(WrapInOuter(BuildSubBoxHeader("Arguments", innerWidth), innerWidth, outerContentWidth));

        // Header row.
        var header = BuildArgsHeader(nameCol, reqCol, typeCol, descCol);
        sb.AppendLine(WrapInOuter($"├{new string('─', innerWidth - 2)}┤", innerWidth, outerContentWidth));
        sb.AppendLine(WrapInOuter($"│ {header}{Pad(contentWidth - VisibleWidthOfArgsHeader(nameCol, reqCol, typeCol, descCol))} │", innerWidth, outerContentWidth));
        sb.AppendLine(WrapInOuter($"├{new string('─', innerWidth - 2)}┤", innerWidth, outerContentWidth));

        for (var ai = 0; ai < args.Count; ai++)
        {
            var a = args[ai];
            var nameCell = ClipWithEllipsis(a.Name, nameCol);
            var nameStyled = Style(FlagStyle, PadRightPlain(nameCell, nameCol));

            var reqStyled = reqCol > 0
                ? (a.Required ? Style(RequiredStyle, PadRightPlain("✓", reqCol))
                              : Style(OptionalMarkStyle, PadRightPlain("·", reqCol)))
                : string.Empty;

            var typeStyled = typeCol > 0
                ? Style(TypeStyle, PadRightPlain(ClipWithEllipsis(a.TypeName ?? "", typeCol), typeCol))
                : string.Empty;

            var descLines = WrapText(a.Description ?? "", descCol).ToList();
            for (var i = 0; i < Math.Max(1, descLines.Count); i++)
            {
                var descLine = i < descLines.Count ? descLines[i] : string.Empty;
                var rowName = i == 0 ? nameStyled : Pad(nameCol);
                var rowReq = i == 0 ? reqStyled : Pad(reqCol);
                var rowType = i == 0 ? typeStyled : Pad(typeCol);
                var rowDesc = PadRightPlain(descLine, descCol);

                var parts = new StringBuilder();
                parts.Append("│ ").Append(rowName);
                if (reqCol > 0) parts.Append("  ").Append(rowReq);
                if (typeCol > 0) parts.Append("  ").Append(rowType);
                parts.Append("  ").Append(rowDesc).Append(" │");

                var visible = nameCol + (reqCol > 0 ? 2 + reqCol : 0) + (typeCol > 0 ? 2 + typeCol : 0) + 2 + descCol + 4;
                sb.AppendLine(WrapInOuter(parts.ToString(), innerWidth, outerContentWidth));
            }

            // Blank separator row between entries so descriptions don't run together.
            if (ai < args.Count - 1)
            {
                sb.AppendLine(WrapInOuter($"│{Pad(innerWidth - 2)}│", innerWidth, outerContentWidth));
            }
        }

        sb.AppendLine(WrapInOuter($"╰{new string('─', innerWidth - 2)}╯", innerWidth, outerContentWidth));
    }

    private static string BuildArgsHeader(int nameCol, int reqCol, int typeCol, int descCol)
    {
        var header = new StringBuilder();
        header.Append(Style(DimStyle, PadRightPlain("Name", nameCol)));
        if (reqCol > 0) header.Append("  ").Append(Style(DimStyle, PadRightPlain("Req", reqCol)));
        if (typeCol > 0) header.Append("  ").Append(Style(DimStyle, PadRightPlain("Type", typeCol)));
        header.Append("  ").Append(Style(DimStyle, PadRightPlain("Description", descCol)));
        return header.ToString();
    }

    private static int VisibleWidthOfArgsHeader(int nameCol, int reqCol, int typeCol, int descCol)
        => nameCol + (reqCol > 0 ? 2 + reqCol : 0) + (typeCol > 0 ? 2 + typeCol : 0) + 2 + descCol;

    private static void RenderOptionsSubBox(
        StringBuilder sb,
        IReadOnlyList<HelpOptionInfo> opts,
        int innerWidth,
        int outerContentWidth)
    {
        var contentWidth = innerWidth - 4;
        var flagCol = Math.Max(6, opts.Max(o => o.Syntax.Length));
        flagCol = Math.Min(flagCol, Math.Max(10, contentWidth * 2 / 5));

        // Decide whether to render a third "Default" column. Only show it when
        // at least one option carries a non-empty default; keep the two-column
        // layout otherwise so common commands stay compact.
        var hasDefaults = opts.Any(o => !string.IsNullOrEmpty(o.Default));
        var defaultCol = 0;
        if (hasDefaults)
        {
            defaultCol = Math.Max(7, opts.Max(o => (o.Default ?? "").Length));
            defaultCol = Math.Min(defaultCol, Math.Max(7, contentWidth / 5));
        }

        // Spacing: "Flag" + "  " + (Default + "  ")? + "Description"
        var spacing = 2 + (defaultCol > 0 ? defaultCol + 2 : 0);
        var descCol = contentWidth - flagCol - spacing;
        if (descCol < 12)
        {
            // Drop the default column if it crowds the description.
            if (defaultCol > 0)
            {
                defaultCol = 0;
                descCol = contentWidth - flagCol - 2;
            }
        }
        if (descCol < 12)
        {
            // Stack mode: flag on one line, description indented on the next.
            sb.AppendLine(WrapInOuter($"╭{new string('─', innerWidth - 2)}╮", innerWidth, outerContentWidth));
            sb.AppendLine(WrapInOuter(BuildSubBoxHeader("Options", innerWidth), innerWidth, outerContentWidth));
            sb.AppendLine(WrapInOuter($"├{new string('─', innerWidth - 2)}┤", innerWidth, outerContentWidth));
            for (var oi = 0; oi < opts.Count; oi++)
            {
                var o = opts[oi];
                var flagStyled = StyleUsageLine(o.Syntax);
                sb.AppendLine(WrapInOuter($"│ {flagStyled}{Pad(contentWidth - o.Syntax.Length)} │", innerWidth, outerContentWidth));
                if (!string.IsNullOrEmpty(o.Default))
                {
                    var defLabel = "  default: ";
                    var defLine = defLabel + o.Default;
                    var combined = Style(DimStyle, defLabel) + Style(TypeStyle, o.Default!);
                    sb.AppendLine(WrapInOuter($"│ {combined}{Pad(contentWidth - defLine.Length)} │", innerWidth, outerContentWidth));
                }
                foreach (var line in WrapText(o.Description ?? "", contentWidth - 2))
                {
                    var styled = "  " + line;
                    sb.AppendLine(WrapInOuter($"│ {styled}{Pad(contentWidth - styled.Length)} │", innerWidth, outerContentWidth));
                }
                if (oi < opts.Count - 1)
                {
                    sb.AppendLine(WrapInOuter($"│{Pad(innerWidth - 2)}│", innerWidth, outerContentWidth));
                }
            }
            sb.AppendLine(WrapInOuter($"╰{new string('─', innerWidth - 2)}╯", innerWidth, outerContentWidth));
            return;
        }

        sb.AppendLine(WrapInOuter($"╭{new string('─', innerWidth - 2)}╮", innerWidth, outerContentWidth));
        sb.AppendLine(WrapInOuter(BuildSubBoxHeader("Options", innerWidth), innerWidth, outerContentWidth));
        sb.AppendLine(WrapInOuter($"├{new string('─', innerWidth - 2)}┤", innerWidth, outerContentWidth));

        // Header row.
        var headerSb = new StringBuilder();
        headerSb.Append(Style(DimStyle, PadRightPlain("Flag", flagCol)));
        if (defaultCol > 0)
            headerSb.Append("  ").Append(Style(DimStyle, PadRightPlain("Default", defaultCol)));
        headerSb.Append("  ").Append(Style(DimStyle, PadRightPlain("Description", descCol)));
        var visibleHeader = flagCol + (defaultCol > 0 ? 2 + defaultCol : 0) + 2 + descCol;
        sb.AppendLine(WrapInOuter($"│ {headerSb}{Pad(contentWidth - visibleHeader)} │", innerWidth, outerContentWidth));
        sb.AppendLine(WrapInOuter($"├{new string('─', innerWidth - 2)}┤", innerWidth, outerContentWidth));

        for (var oi = 0; oi < opts.Count; oi++)
        {
            var o = opts[oi];
            var flagPlain = ClipWithEllipsis(o.Syntax, flagCol);
            var flagStyled = StyleUsageLine(flagPlain) + Pad(flagCol - flagPlain.Length);

            string defaultStyled = string.Empty;
            if (defaultCol > 0)
            {
                var defText = ClipWithEllipsis(o.Default ?? "", defaultCol);
                defaultStyled = string.IsNullOrEmpty(defText)
                    ? Style(OptionalMarkStyle, PadRightPlain("·", defaultCol))
                    : Style(TypeStyle, PadRightPlain(defText, defaultCol));
            }

            var descLines = WrapText(o.Description ?? "", descCol).ToList();
            for (var i = 0; i < Math.Max(1, descLines.Count); i++)
            {
                var descLine = i < descLines.Count ? descLines[i] : string.Empty;
                var leftCell = i == 0 ? flagStyled : Pad(flagCol);
                var defCell = i == 0 ? defaultStyled : Pad(defaultCol);
                var rowSb = new StringBuilder();
                rowSb.Append("│ ").Append(leftCell);
                if (defaultCol > 0) rowSb.Append("  ").Append(defCell);
                rowSb.Append("  ").Append(PadRightPlain(descLine, descCol)).Append(" │");
                sb.AppendLine(WrapInOuter(rowSb.ToString(), innerWidth, outerContentWidth));
            }
            if (oi < opts.Count - 1)
            {
                sb.AppendLine(WrapInOuter($"│{Pad(innerWidth - 2)}│", innerWidth, outerContentWidth));
            }
        }

        sb.AppendLine(WrapInOuter($"╰{new string('─', innerWidth - 2)}╯", innerWidth, outerContentWidth));
    }

    private static void RenderPipelineSubBox(
        StringBuilder sb,
        HelpPipelineInputInfo? input,
        string? output,
        string? streaming,
        int innerWidth,
        int outerContentWidth)
    {
        var contentWidth = innerWidth - 4;

        sb.AppendLine(WrapInOuter($"╭{new string('─', innerWidth - 2)}╮", innerWidth, outerContentWidth));
        sb.AppendLine(WrapInOuter(BuildSubBoxHeader("Pipeline", innerWidth), innerWidth, outerContentWidth));
        sb.AppendLine(WrapInOuter($"├{new string('─', innerWidth - 2)}┤", innerWidth, outerContentWidth));

        if (input is not null)
        {
            var kinds = new List<string>();
            if (input.Object) kinds.Add("object");
            if (input.Scalar) kinds.Add("scalar");
            if (input.PathLike) kinds.Add("path-like");
            if (input.Collection) kinds.Add("collection");
            if (kinds.Count == 0) kinds.Add("none");

            var label = "Accepts: ";
            var values = string.Join(" · ", kinds);
            var line = label + values;
            foreach (var wrapped in WrapText(line, contentWidth))
            {
                if (wrapped.StartsWith(label))
                {
                    var combined = Style(DimStyle, label) + Style(TypeStyle, wrapped[label.Length..]);
                    sb.AppendLine(WrapInOuter($"│ {combined}{Pad(contentWidth - wrapped.Length)} │", innerWidth, outerContentWidth));
                }
                else
                {
                    sb.AppendLine(WrapInOuter($"│ {Style(TypeStyle, wrapped)}{Pad(contentWidth - wrapped.Length)} │", innerWidth, outerContentWidth));
                }
            }

            if (!string.IsNullOrWhiteSpace(input.Notes))
            {
                foreach (var wrapped in WrapText(input.Notes!, contentWidth))
                {
                    sb.AppendLine(WrapInOuter($"│ {Style(FootnoteStyle, wrapped)}{Pad(contentWidth - wrapped.Length)} │", innerWidth, outerContentWidth));
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(output))
        {
            var label = "Output:  ";
            var line = label + output;
            foreach (var wrapped in WrapText(line, contentWidth))
            {
                if (wrapped.StartsWith(label))
                {
                    var combined = Style(DimStyle, label) + Style(TypeStyle, wrapped[label.Length..]);
                    sb.AppendLine(WrapInOuter($"│ {combined}{Pad(contentWidth - wrapped.Length)} │", innerWidth, outerContentWidth));
                }
                else
                {
                    sb.AppendLine(WrapInOuter($"│ {Style(TypeStyle, wrapped)}{Pad(contentWidth - wrapped.Length)} │", innerWidth, outerContentWidth));
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(streaming))
        {
            var label = "Stream:  ";
            var line = label + streaming;
            foreach (var wrapped in WrapText(line, contentWidth))
            {
                if (wrapped.StartsWith(label))
                {
                    var combined = Style(DimStyle, label) + Style(TypeStyle, wrapped[label.Length..]);
                    sb.AppendLine(WrapInOuter($"│ {combined}{Pad(contentWidth - wrapped.Length)} │", innerWidth, outerContentWidth));
                }
                else
                {
                    sb.AppendLine(WrapInOuter($"│ {Style(TypeStyle, wrapped)}{Pad(contentWidth - wrapped.Length)} │", innerWidth, outerContentWidth));
                }
            }
        }

        sb.AppendLine(WrapInOuter($"╰{new string('─', innerWidth - 2)}╯", innerWidth, outerContentWidth));
    }

    private static void RenderExamplesSubBox(
        StringBuilder sb,
        HelpTopic topic,
        Func<string, string>? codeHighlighter,
        int innerWidth,
        int outerContentWidth)
    {
        var contentWidth = innerWidth - 4;

        sb.AppendLine(WrapInOuter($"╭{new string('─', innerWidth - 2)}╮", innerWidth, outerContentWidth));
        sb.AppendLine(WrapInOuter(BuildSubBoxHeader("Examples", innerWidth), innerWidth, outerContentWidth));
        sb.AppendLine(WrapInOuter($"├{new string('─', innerWidth - 2)}┤", innerWidth, outerContentWidth));

        var bullet = Style(ExampleBulletStyle, "▸ ");
        const int bulletWidth = 2;

        if (topic.ExampleItems is { Count: > 0 } structured)
        {
            for (var i = 0; i < structured.Count; i++)
            {
                if (i > 0)
                {
                    sb.AppendLine(WrapInOuter($"│ {Pad(contentWidth)} │", innerWidth, outerContentWidth));
                }
                var ex = structured[i];

                if (!string.IsNullOrWhiteSpace(ex.Title))
                {
                    foreach (var line in WrapText(ex.Title!, contentWidth))
                    {
                        var styled = Style(SectionHeaderStyle with { Underline = false, Foreground = "yellow" }, line);
                        sb.AppendLine(WrapInOuter($"│ {styled}{Pad(contentWidth - line.Length)} │", innerWidth, outerContentWidth));
                    }
                }

                EmitExampleCode(sb, ex.Code, codeHighlighter, contentWidth, innerWidth, outerContentWidth, bullet, bulletWidth);

                if (!string.IsNullOrWhiteSpace(ex.Description))
                {
                    foreach (var line in WrapText(ex.Description!, contentWidth - bulletWidth))
                    {
                        var styled = Pad(bulletWidth) + Style(ExampleDescStyle, line);
                        sb.AppendLine(WrapInOuter($"│ {styled}{Pad(contentWidth - bulletWidth - line.Length)} │", innerWidth, outerContentWidth));
                    }
                }
            }
        }
        else if (topic.Examples is { Count: > 0 } plain)
        {
            for (var i = 0; i < plain.Count; i++)
            {
                if (i > 0)
                {
                    sb.AppendLine(WrapInOuter($"│ {Pad(contentWidth)} │", innerWidth, outerContentWidth));
                }
                EmitExampleCode(sb, plain[i], codeHighlighter, contentWidth, innerWidth, outerContentWidth, bullet, bulletWidth);
            }
        }

        sb.AppendLine(WrapInOuter($"╰{new string('─', innerWidth - 2)}╯", innerWidth, outerContentWidth));
    }

    private static void EmitExampleCode(
        StringBuilder sb,
        string code,
        Func<string, string>? codeHighlighter,
        int contentWidth,
        int innerWidth,
        int outerContentWidth,
        string bullet,
        int bulletWidth)
    {
        var codeWidth = contentWidth - bulletWidth;
        // Wrap the *plain* text by visible columns, then highlight each wrapped line.
        var lines = WrapText(code, codeWidth).ToList();
        for (var li = 0; li < lines.Count; li++)
        {
            var plain = lines[li];
            var highlighted = codeHighlighter is not null ? SafeHighlight(codeHighlighter, plain) : plain;
            var prefix = li == 0 ? bullet : Pad(bulletWidth);
            var pad = Pad(codeWidth - plain.Length);
            sb.AppendLine(WrapInOuter($"│ {prefix}{highlighted}{pad} │", innerWidth, outerContentWidth));
        }
    }

    private static void RenderRelatedSubBox(
        StringBuilder sb,
        IReadOnlyList<string> related,
        int innerWidth,
        int outerContentWidth)
    {
        var contentWidth = innerWidth - 4;

        sb.AppendLine(WrapInOuter($"╭{new string('─', innerWidth - 2)}╮", innerWidth, outerContentWidth));
        sb.AppendLine(WrapInOuter(BuildSubBoxHeader("Related", innerWidth), innerWidth, outerContentWidth));
        sb.AppendLine(WrapInOuter($"├{new string('─', innerWidth - 2)}┤", innerWidth, outerContentWidth));

        // Build one logical line "a · b · c" then word-wrap on the dot.
        var joinedPlain = string.Join(" · ", related);
        foreach (var line in WrapText(joinedPlain, contentWidth))
        {
            var styled = StyleRelatedLine(line);
            sb.AppendLine(WrapInOuter($"│ {styled}{Pad(contentWidth - line.Length)} │", innerWidth, outerContentWidth));
        }

        sb.AppendLine(WrapInOuter($"╰{new string('─', innerWidth - 2)}╯", innerWidth, outerContentWidth));
    }

    private static string StyleRelatedLine(string line)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < line.Length)
        {
            if (i + 2 < line.Length && line[i] == ' ' && line[i + 1] == '·' && line[i + 2] == ' ')
            {
                sb.Append(' ');
                sb.Append(Style(RelatedDotStyle, "·"));
                sb.Append(' ');
                i += 3;
            }
            else
            {
                var start = i;
                while (i < line.Length && !(i + 2 < line.Length && line[i] == ' ' && line[i + 1] == '·' && line[i + 2] == ' '))
                {
                    i++;
                }
                sb.Append(Style(RelatedItemStyle, line[start..i]));
            }
        }
        return sb.ToString();
    }

    // ─── Title helpers ───────────────────────────────────────────────────────

    private static string BuildTitlePlain(HelpTopic topic)
    {
        var kindLabel = FormatKind(topic.Kind);
        var sb = new StringBuilder();
        sb.Append(topic.Name);
        if (!string.IsNullOrWhiteSpace(kindLabel))
        {
            sb.Append(" │ ").Append(kindLabel);
        }
        if (!string.IsNullOrWhiteSpace(topic.Category))
        {
            sb.Append(" · ").Append(topic.Category);
        }
        return sb.ToString();
    }

    private static (string Styled, int Visible) StyleTitle(HelpTopic topic)
    {
        var kindLabel = FormatKind(topic.Kind);
        var nameStyled = Style(TitleNameStyle, topic.Name);
        var sep1 = !string.IsNullOrWhiteSpace(kindLabel) ? Style(TitleSeparatorStyle, " │ ") : string.Empty;
        var kindStyled = !string.IsNullOrWhiteSpace(kindLabel) ? Style(TitleKindStyle, kindLabel) : string.Empty;
        var sep2 = !string.IsNullOrWhiteSpace(topic.Category) ? Style(TitleSeparatorStyle, " · ") : string.Empty;
        var catStyled = !string.IsNullOrWhiteSpace(topic.Category) ? Style(TitleCategoryStyle, topic.Category) : string.Empty;
        var styled = nameStyled + sep1 + kindStyled + sep2 + catStyled;
        var visible = topic.Name.Length
            + (string.IsNullOrWhiteSpace(kindLabel) ? 0 : 3 + kindLabel.Length)
            + (string.IsNullOrWhiteSpace(topic.Category) ? 0 : 3 + topic.Category.Length);
        return (styled, visible);
    }

    private static string FormatKind(HelpSubjectKind kind)
        => kind switch
        {
            HelpSubjectKind.BuiltIn => "Built-in",
            HelpSubjectKind.Function => "Function",
            HelpSubjectKind.Alias => "Alias",
            HelpSubjectKind.External => "External",
            HelpSubjectKind.Language => "Language",
            HelpSubjectKind.Type => "Type",
            HelpSubjectKind.DiagnosticCode => "Diagnostic",
            _ => kind.ToString(),
        };

    // ─── Usage tokenisation / styling ────────────────────────────────────────

    // Lightweight tokenizer: walks the line and styles
    //   "-x", "--long"    → flag
    //   "<x>", "[x]"      → placeholder (italic dim)
    //   "a|b|c"           → choices (split on |, each magenta)
    //   "..."             → dim
    //   everything else   → default
    //
    // Brackets themselves are kept dim. The result preserves visible width
    // identical to the input.
    private static string StyleUsageLine(string line)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < line.Length)
        {
            var c = line[i];

            // Long run of ".":
            if (c == '.' && i + 1 < line.Length && line[i + 1] == '.')
            {
                var start = i;
                while (i < line.Length && line[i] == '.') i++;
                sb.Append(Style(DimStyle, line[start..i]));
                continue;
            }

            if (c == '[' || c == '<')
            {
                var open = c;
                var close = c == '[' ? ']' : '>';
                var end = line.IndexOf(close, i + 1);
                if (end < 0)
                {
                    sb.Append(Style(DimStyle, c.ToString()));
                    i++;
                    continue;
                }
                var inner = line[(i + 1)..end];
                sb.Append(Style(DimStyle, open.ToString()));
                sb.Append(StyleUsageBracketBody(inner, open == '<'));
                sb.Append(Style(DimStyle, close.ToString()));
                i = end + 1;
                continue;
            }

            if (c == '-' && (i == 0 || !char.IsLetterOrDigit(line[i - 1])))
            {
                // Flag token: read until whitespace or a bracket boundary.
                var start = i;
                i++;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '-' || line[i] == '_'))
                {
                    i++;
                }
                sb.Append(Style(FlagStyle, line[start..i]));
                continue;
            }

            // Plain run.
            var runStart = i;
            while (i < line.Length
                   && line[i] != '['
                   && line[i] != '<'
                   && !(line[i] == '.' && i + 1 < line.Length && line[i + 1] == '.')
                   && !(line[i] == '-' && (i == 0 || !char.IsLetterOrDigit(line[i - 1])) && i + 1 < line.Length && (char.IsLetterOrDigit(line[i + 1]) || line[i + 1] == '-')))
            {
                i++;
            }
            sb.Append(line[runStart..i]);
        }
        return sb.ToString();
    }

    private static string StyleUsageBracketBody(string inner, bool isAngle)
    {
        // If the inner contains pipe-separated choices that are all simple words,
        // style each segment with ChoiceStyle and the pipes dim.
        if (inner.Contains('|') && !inner.Any(ch => ch is '<' or '>' or '[' or ']'))
        {
            var parts = inner.Split('|');
            if (parts.All(p => p.Length > 0 && p.All(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')))
            {
                var sb = new StringBuilder();
                for (var j = 0; j < parts.Length; j++)
                {
                    if (j > 0) sb.Append(Style(DimStyle, "|"));
                    sb.Append(Style(ChoiceStyle, parts[j]));
                }
                return sb.ToString();
            }
        }
        // Otherwise style the inside as a placeholder, but recursively style
        // any nested flag-like tokens, "...", and nested bracket groups inside.
        return StyleNestedBracketContent(inner);
    }

    private static string StyleNestedBracketContent(string inner)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < inner.Length)
        {
            var c = inner[i];
            if (c == '.' && i + 1 < inner.Length && inner[i + 1] == '.')
            {
                var start = i;
                while (i < inner.Length && inner[i] == '.') i++;
                sb.Append(Style(DimStyle, inner[start..i]));
                continue;
            }
            if (c == '-' && (i == 0 || !char.IsLetterOrDigit(inner[i - 1])))
            {
                var start = i;
                i++;
                while (i < inner.Length && (char.IsLetterOrDigit(inner[i]) || inner[i] == '-' || inner[i] == '_')) i++;
                sb.Append(Style(FlagStyle, inner[start..i]));
                continue;
            }
            if (c == '[' || c == '<')
            {
                var open = c;
                var close = c == '[' ? ']' : '>';
                var end = inner.IndexOf(close, i + 1);
                if (end < 0) { sb.Append(Style(DimStyle, c.ToString())); i++; continue; }
                var sub = inner[(i + 1)..end];
                sb.Append(Style(DimStyle, open.ToString()));
                sb.Append(StyleUsageBracketBody(sub, open == '<'));
                sb.Append(Style(DimStyle, close.ToString()));
                i = end + 1;
                continue;
            }
            // Plain run inside the placeholder body — italic dim.
            var runStart = i;
            while (i < inner.Length
                   && inner[i] != '['
                   && inner[i] != '<'
                   && !(inner[i] == '.' && i + 1 < inner.Length && inner[i + 1] == '.')
                   && !(inner[i] == '-' && (i == 0 || !char.IsLetterOrDigit(inner[i - 1])) && i + 1 < inner.Length && (char.IsLetterOrDigit(inner[i + 1]) || inner[i + 1] == '-')))
            {
                i++;
            }
            sb.Append(Style(PlaceholderStyle, inner[runStart..i]));
        }
        return sb.ToString();
    }

    // ─── Outer width estimators ──────────────────────────────────────────────

    private static int ComputeUsageDesiredOuter(string? usage)
    {
        if (string.IsNullOrWhiteSpace(usage)) return 0;
        // We wrap usage anyway, but try to keep first words on one line.
        var firstSegment = usage!.Split(' ').FirstOrDefault() ?? string.Empty;
        return Math.Min(80, firstSegment.Length + 16) + InnerMargin + OuterWall + 4;
    }

    private static int ComputeArgumentsDesiredOuter(IReadOnlyList<HelpArgumentInfo>? args)
    {
        if (args is null || args.Count == 0) return 0;
        var name = args.Max(a => a.Name.Length);
        var type = args.Max(a => (a.TypeName ?? "").Length);
        return name + 3 /*req*/ + type + 30 /*desc rough*/ + 12;
    }

    private static int ComputeOptionsDesiredOuter(IReadOnlyList<HelpOptionInfo>? opts)
    {
        if (opts is null || opts.Count == 0) return 0;
        var flag = opts.Max(o => o.Syntax.Length);
        var defaults = opts.Any(o => !string.IsNullOrEmpty(o.Default))
            ? opts.Max(o => (o.Default ?? "").Length) + 2
            : 0;
        return flag + defaults + 36 + 8;
    }

    private static int ComputePipelineDesiredOuter(HelpPipelineInputInfo? input, string? output)
    {
        if (input is null && string.IsNullOrWhiteSpace(output)) return 0;
        var outLen = (output?.Length ?? 0) + 12;
        return Math.Min(80, outLen + 8);
    }

    private static int ComputeRelatedDesiredOuter(IReadOnlyList<string>? related)
    {
        if (related is null || related.Count == 0) return 0;
        var widest = related.Max(r => r.Length);
        return Math.Min(80, widest * 2 + 12);
    }

    private static int ComputePathDesiredOuter(string? path)
        => string.IsNullOrWhiteSpace(path) ? 0 : Math.Min(120, path!.Length + 12);

    private static int ComputeAliasesDesiredOuter(IReadOnlyList<string>? aliases)
    {
        if (aliases is null || aliases.Count == 0) return 0;
        var total = aliases.Sum(a => a.Length + 2) + 12;
        return Math.Min(80, total);
    }

    // ─── Sub-box header ──────────────────────────────────────────────────────

    private static string BuildSubBoxHeader(string title, int innerWidth)
    {
        var contentWidth = innerWidth - 4;
        var titleStyled = Style(SectionHeaderStyle, title);
        var pad = Pad(contentWidth - title.Length);
        return $"│ {titleStyled}{pad} │";
    }

    // ─── Outer box utilities (shared shape with RuntimeNamespaceSummaryRenderer) ──

    private static string BuildTop(int width)
        => $"╭{new string('─', width - 2)}╮";

    private static string BuildBottom(int width)
        => $"╰{new string('─', width - 2)}╯";

    private static string BuildOuterSeparator(int width)
        => $"├{new string('─', width - 2)}┤";

    private static string BuildOuterRow(string content, int contentWidth)
    {
        var clipped = ClipWithEllipsis(content, contentWidth - 2);
        return $"│ {PadRightPlain(clipped, contentWidth - 2)} │";
    }

    private static string BuildOuterRowStyled(string styledContent, int plainVisibleLen, int contentWidth)
    {
        var innerWidth = contentWidth - 2;
        if (plainVisibleLen > innerWidth)
        {
            var plain = StyledText.StripAnsi(styledContent);
            var clipped = ClipWithEllipsis(plain, innerWidth);
            return $"│ {PadRightPlain(clipped, innerWidth)} │";
        }
        var padding = Pad(innerWidth - plainVisibleLen);
        return $"│ {styledContent}{padding} │";
    }

    private static string WrapInOuter(string innerLine, int innerVisibleWidth, int outerContentWidth)
    {
        var targetContent = outerContentWidth - 2;
        var padding = Pad(Math.Max(0, targetContent - innerVisibleWidth));
        return $"│ {innerLine}{padding} │";
    }

    private static string Pad(int n) => n <= 0 ? string.Empty : new string(' ', n);

    private static string PadRightPlain(string text, int width)
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
        if (remaining.Length >= 0) yield return remaining;
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

    private static string SafeHighlight(Func<string, string> highlighter, string text)
    {
        try
        {
            return highlighter(text);
        }
        catch
        {
            return text;
        }
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
