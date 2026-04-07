using System.Diagnostics;
using System.Text;
using System.Threading;
using Tosh.Core;

namespace Tosh.Cli;

internal sealed partial class ConsoleInlinePromptProvider
{
    private static readonly long HelpFilterBatchWindowTicks = (long)(Stopwatch.Frequency * 0.012);

    public void BrowseHelp(string? initialQuery = null, string? initialTopicName = null)
    {
        if (_runtime is null)
        {
            return;
        }

        var state = new HelpTreeState(_runtime, initialQuery, initialTopicName);
        RunHelpLoop(state);
    }

    private void RunHelpLoop(HelpTreeState state)
    {
        var tableTheme = _runtime?.Config.Theme.Tables ?? new ToshTableThemeConfig();
        var tuiTheme = _runtime?.Config.Theme.Tui ?? new ToshTuiThemeConfig();
        var box = InlineTablePlan.GetBoxCharacters(tableTheme.BoxStyle);
        var borderStyle = tableTheme.Border;
        var sectionStyle = tableTheme.Header;
        var selectionStyle = tableTheme.Selection;
        var titleStyle = tuiTheme.Title;
        var footerStyle = tuiTheme.Footer;
        var textStyle = new ToshTextStyleConfig();
        var labelStyle = new ToshTextStyleConfig(bold: true);
        var signatureStyle = new ToshTextStyleConfig(foreground: "cyan");
        var metadataStyle = new ToshTextStyleConfig(foreground: "magenta");
        var descriptionStyle = new ToshTextStyleConfig(foreground: "green");
        var mutedValueStyle = new ToshTextStyleConfig(dim: true);
        var pageSize = GetHelpPageSize();
        const int detailLineCount = 5;
        var totalLines = pageSize + detailLineCount + 8;
        var viewOffset = 0;
        var firstRender = true;
        var editingFilter = false;
        var filterBuffer = new StringBuilder(state.Filter);
        string? filterSnapshot = null;

        Console.Write(HideCursor);

        try
        {
            while (true)
            {
                var innerWidth = Math.Max(52, (TryGetConsoleWidth() ?? 100) - 2);
                var title = " help --cli ";
                var visibleNodes = state.VisibleNodes;
                var selectedIndex = state.SelectedIndex;
                var visibleTopicCount = visibleNodes.Count(node => node.Kind == HelpTreeNodeKind.Topic);

                if (selectedIndex < viewOffset)
                {
                    viewOffset = selectedIndex;
                }

                if (selectedIndex >= viewOffset + pageSize)
                {
                    viewOffset = selectedIndex - pageSize + 1;
                }

                viewOffset = Math.Max(0, viewOffset);

                if (firstRender)
                {
                    ReserveLines(totalLines);
                    MoveUp(totalLines);
                    firstRender = false;
                }
                else
                {
                    Console.Write("\r");
                    MoveUp(totalLines - 1);
                }

                var frameBuffer = new StringBuilder();
                frameBuffer.Append(ClearLine);
                frameBuffer.AppendLine(BuildInspectBorderLine(box.TopLeft, box.TopRight, box.Horizontal, title, innerWidth, borderStyle, titleStyle));

                frameBuffer.Append(ClearLine);
                frameBuffer.AppendLine(BuildInspectStyledContentRow(
                    BuildHelpSearchSegments(state.Filter, visibleTopicCount, state.TotalTopicCount, labelStyle, signatureStyle, footerStyle),
                    innerWidth,
                    borderStyle));

                frameBuffer.Append(ClearLine);
                frameBuffer.AppendLine(BuildInspectContentRow(
                    " fuzzy tree of commands, language topics, shell types, and resolved CLR/external topics",
                    innerWidth,
                    borderStyle,
                    footerStyle));

                frameBuffer.Append(ClearLine);
                frameBuffer.AppendLine(borderStyle.Apply($"{box.MiddleLeft}{new string(box.Horizontal, innerWidth)}{box.MiddleRight}").ToAnsi());

                for (var row = 0; row < pageSize; row++)
                {
                    var visibleIndex = viewOffset + row;
                    string line;

                    if (visibleNodes.Count == 0)
                    {
                        line = row == 0
                            ? BuildInspectContentRow("<no matching help topics>", innerWidth, borderStyle, new ToshTextStyleConfig(foreground: "red", dim: true))
                            : BuildInspectContentRow(string.Empty, innerWidth, borderStyle, footerStyle);
                    }
                    else if (visibleIndex < visibleNodes.Count)
                    {
                        line = BuildHelpNodeRow(
                            visibleNodes[visibleIndex],
                            visibleIndex == selectedIndex,
                            innerWidth,
                            borderStyle,
                            sectionStyle,
                            selectionStyle,
                            labelStyle,
                            metadataStyle,
                            descriptionStyle,
                            mutedValueStyle);
                    }
                    else
                    {
                        line = BuildInspectContentRow(string.Empty, innerWidth, borderStyle, footerStyle);
                    }

                    frameBuffer.Append(ClearLine);
                    frameBuffer.AppendLine(line);
                }

                frameBuffer.Append(ClearLine);
                frameBuffer.AppendLine(borderStyle.Apply($"{box.MiddleLeft}{new string(box.Horizontal, innerWidth)}{box.MiddleRight}").ToAnsi());

                var detailRows = BuildHelpDetailRows(state.SelectedNode, labelStyle, signatureStyle, metadataStyle, descriptionStyle, textStyle, mutedValueStyle);

                foreach (var detailRow in detailRows)
                {
                    frameBuffer.Append(ClearLine);
                    frameBuffer.AppendLine(BuildInspectStyledContentRow(detailRow, innerWidth, borderStyle));
                }

                frameBuffer.Append(ClearLine);
                frameBuffer.AppendLine(BuildInspectContentRow(BuildHelpStatusText(state), innerWidth, borderStyle, footerStyle));

                var footerText = editingFilter
                    ? $"/ filter: {filterBuffer}  enter apply  esc cancel"
                    : " ↑/↓ navigate  ←/→ collapse/expand  i insert  / filter  q quit";
                frameBuffer.Append(ClearLine);
                frameBuffer.AppendLine(BuildInspectContentRow(footerText, innerWidth, borderStyle, footerStyle));

                frameBuffer.Append(ClearLine);
                frameBuffer.Append(borderStyle.Apply($"{box.BottomLeft}{new string(box.Horizontal, innerWidth)}{box.BottomRight}").ToAnsi());
                Console.Write(frameBuffer.ToString());

                var key = Console.ReadKey(intercept: true);

                if (!editingFilter && key.KeyChar == '/')
                {
                    editingFilter = true;
                    filterSnapshot = state.Filter;
                    filterBuffer.Clear();
                    filterBuffer.Append(state.Filter);
                    PreviewHelpFilterFooter(filterBuffer.ToString(), innerWidth, borderStyle, footerStyle);
                    var filterDirty = false;
                    DrainHelpFilterInput(state, filterBuffer, innerWidth, borderStyle, footerStyle, ref editingFilter, ref filterSnapshot, ref filterDirty);

                    if (filterDirty)
                    {
                        state.SetFilter(filterBuffer.ToString());
                    }

                    continue;
                }

                if (editingFilter)
                {
                    var filterDirty = false;
                    ProcessHelpFilterKey(state, filterBuffer, key, innerWidth, borderStyle, footerStyle, ref editingFilter, ref filterSnapshot, ref filterDirty);
                    DrainHelpFilterInput(state, filterBuffer, innerWidth, borderStyle, footerStyle, ref editingFilter, ref filterSnapshot, ref filterDirty);

                    if (filterDirty)
                    {
                        state.SetFilter(filterBuffer.ToString());
                    }

                    continue;
                }

                switch (key.Key)
                {
                    case ConsoleKey.Insert or ConsoleKey.I:
                        if (TryInsertHelpSelection(state))
                        {
                            CleanupLines(totalLines);
                            WriteHelpSummary(state, visibleTopicCount, titleStyle, footerStyle);
                            return;
                        }

                        break;
                    case ConsoleKey.UpArrow or ConsoleKey.K:
                        state.MoveUp();
                        break;
                    case ConsoleKey.DownArrow or ConsoleKey.J:
                        state.MoveDown();
                        break;
                    case ConsoleKey.PageUp:
                        state.MovePageUp(pageSize);
                        break;
                    case ConsoleKey.PageDown:
                        state.MovePageDown(pageSize);
                        break;
                    case ConsoleKey.Home:
                        state.MoveHome();
                        break;
                    case ConsoleKey.End:
                        state.MoveEnd();
                        break;
                    case ConsoleKey.RightArrow or ConsoleKey.Enter:
                        state.ExpandSelected();
                        break;
                    case ConsoleKey.LeftArrow:
                        state.CollapseSelected();
                        break;
                    case ConsoleKey.Escape or ConsoleKey.Q:
                        CleanupLines(totalLines);
                        WriteHelpSummary(state, visibleTopicCount, titleStyle, footerStyle);
                        return;
                }
            }
        }
        finally
        {
            Console.Write(ShowCursor);
        }
    }

    private bool TryInsertHelpSelection(HelpTreeState state)
    {
        var text = BuildHelpInsertionText(state);
        return !string.IsNullOrWhiteSpace(text) && _runtime?.CommandLineInsertion?.TryInsertText(text) == true;
    }

    internal static string? BuildHelpInsertionText(HelpTreeState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.GetSelectedInsertionText();
    }

    private static string BuildHelpNodeRow(
        HelpTreeVisibleNode visibleNode,
        bool selected,
        int innerWidth,
        ToshTextStyleConfig borderStyle,
        ToshTextStyleConfig sectionStyle,
        ToshTextStyleConfig selectionStyle,
        ToshTextStyleConfig nameStyle,
        ToshTextStyleConfig metadataStyle,
        ToshTextStyleConfig descriptionStyle,
        ToshTextStyleConfig mutedStyle)
    {
        if (visibleNode.Kind == HelpTreeNodeKind.Category)
        {
            var glyph = visibleNode.IsExpanded ? "▼" : "▶";
            var countText = visibleNode.VisibleCount != visibleNode.TotalCount
                ? $" ({visibleNode.VisibleCount}/{visibleNode.TotalCount})"
                : $" ({visibleNode.TotalCount})";
            var text = $"{glyph} {visibleNode.Category}{countText}";
            var clipped = ClipPlainText(text, innerWidth);
            var padded = InlineTablePlan.PadRight(clipped, innerWidth);
            var style = selected ? ApplySelectionOverlay(sectionStyle, selectionStyle) : sectionStyle;
            var categoryContent = style.Apply(padded).ToAnsi();
            var v = borderStyle.Apply("│").ToAnsi();
            return $"{v}{categoryContent}{v}";
        }

        var topic = visibleNode.Topic!;
        var segments = new List<(string Text, ToshTextStyleConfig Style)>
        {
            (new string(' ', visibleNode.Depth * 2), mutedStyle),
            ("• ", mutedStyle),
            (topic.Name, nameStyle),
            (" : ", mutedStyle),
            (topic.Kind.ToString(), metadataStyle),
        };

        if (!string.IsNullOrWhiteSpace(topic.Description))
        {
            segments.Add((" = ", mutedStyle));
            segments.Add((topic.Description, descriptionStyle));
        }

        if (selected)
        {
            segments = segments
                .Select(segment => (segment.Text, ApplySelectionOverlay(segment.Style, selectionStyle)))
                .ToList();
        }

        var content = RenderInspectStyledSegments(segments, innerWidth);
        var border = borderStyle.Apply("│").ToAnsi();
        return $"{border}{content}{border}";
    }

    private static IReadOnlyList<IReadOnlyList<(string Text, ToshTextStyleConfig Style)>> BuildHelpDetailRows(
        HelpTreeVisibleNode? selectedNode,
        ToshTextStyleConfig labelStyle,
        ToshTextStyleConfig signatureStyle,
        ToshTextStyleConfig metadataStyle,
        ToshTextStyleConfig descriptionStyle,
        ToshTextStyleConfig textStyle,
        ToshTextStyleConfig mutedStyle)
    {
        if (selectedNode is null)
        {
            return
            [
                BuildHelpDetailRow("selected", "<none>", labelStyle, mutedStyle),
                BuildHelpDetailRow("usage", "<none>", labelStyle, mutedStyle),
                BuildHelpDetailRow("about", "<none>", labelStyle, mutedStyle),
                BuildHelpDetailRow("meta", "<none>", labelStyle, mutedStyle),
                BuildHelpDetailRow("hint", "use / to fuzzy-filter topics", labelStyle, mutedStyle),
            ];
        }

        if (selectedNode.Kind == HelpTreeNodeKind.Category)
        {
            var countText = selectedNode.VisibleCount != selectedNode.TotalCount
                ? $"{selectedNode.VisibleCount} visible of {selectedNode.TotalCount}"
                : $"{selectedNode.TotalCount} topics";

            return
            [
                BuildHelpDetailRow("selected", selectedNode.Category, labelStyle, metadataStyle),
                BuildHelpDetailRow("topics", countText, labelStyle, textStyle),
                BuildHelpDetailRow("state", selectedNode.IsExpanded ? "expanded" : "collapsed", labelStyle, textStyle),
                BuildHelpDetailRow("hint", "→ expand  ← collapse  / fuzzy-filter", labelStyle, mutedStyle),
                BuildHelpDetailRow("detail", "select a topic to see signature, notes, and examples", labelStyle, mutedStyle),
            ];
        }

        var topic = selectedNode.Topic!;
        var aliases = topic.Aliases.Count > 0 ? string.Join(", ", topic.Aliases) : "<none>";
        var meta = $"{topic.Kind}  •  {topic.Category}";

        if (!string.IsNullOrWhiteSpace(topic.Path))
        {
            meta += $"  •  {topic.Path}";
        }

        var notesOrOutput = !string.IsNullOrWhiteSpace(topic.Output)
            ? topic.Output!
            : !string.IsNullOrWhiteSpace(topic.Notes)
                ? topic.Notes!
                : "<none>";

        var exampleOrRelated = topic.Examples.Count > 0
            ? topic.Examples[0]
            : topic.Related.Count > 0
                ? string.Join(", ", topic.Related)
                : "<none>";

        return
        [
            BuildHelpDetailRow("usage", topic.Usage, labelStyle, signatureStyle),
            BuildHelpDetailRow("about", topic.Description, labelStyle, descriptionStyle),
            BuildHelpDetailRow("meta", meta, labelStyle, metadataStyle),
            BuildHelpDetailRow(topic.Output is not null ? "output" : "notes", notesOrOutput, labelStyle, textStyle),
            BuildHelpDetailRow(topic.Examples.Count > 0 ? "example" : "related", exampleOrRelated, labelStyle, topic.Examples.Count > 0 ? descriptionStyle : mutedStyle, $"  aliases: {aliases}", mutedStyle),
        ];
    }

    private static IReadOnlyList<(string Text, ToshTextStyleConfig Style)> BuildHelpDetailRow(
        string label,
        string value,
        ToshTextStyleConfig labelStyle,
        ToshTextStyleConfig valueStyle,
        string? suffix = null,
        ToshTextStyleConfig? suffixStyle = null)
    {
        var segments = new List<(string Text, ToshTextStyleConfig Style)>
        {
            ($" {label}: ", labelStyle),
            (value, valueStyle),
        };

        if (!string.IsNullOrWhiteSpace(suffix))
        {
            segments.Add((suffix, suffixStyle ?? valueStyle));
        }

        return segments;
    }

    private static IReadOnlyList<(string Text, ToshTextStyleConfig Style)> BuildHelpSearchSegments(
        string filter,
        int visibleTopicCount,
        int totalTopicCount,
        ToshTextStyleConfig labelStyle,
        ToshTextStyleConfig queryStyle,
        ToshTextStyleConfig mutedStyle)
    {
        var query = string.IsNullOrWhiteSpace(filter) ? "<all topics>" : filter;
        var count = string.IsNullOrWhiteSpace(filter)
            ? $" ({totalTopicCount} topics)"
            : $" ({visibleTopicCount} matches)";

        return
        [
            (" search: ", labelStyle),
            (query, queryStyle),
            (count, mutedStyle),
        ];
    }

    private static string BuildHelpStatusText(HelpTreeState state)
    {
        var visible = state.VisibleNodes;
        var position = visible.Count == 0 ? "0/0" : $"{state.SelectedIndex + 1}/{visible.Count}";
        var kind = state.SelectedNode?.Kind.ToString().ToLowerInvariant() ?? "<none>";
        var path = BuildHelpSelectedPath(visible, state.SelectedIndex);
        var filterHint = string.IsNullOrWhiteSpace(state.Filter) ? string.Empty : $"  filter: {state.Filter}";
        return $" status: {position}  kind {kind}  path {path}{filterHint}";
    }

    private static string BuildHelpSelectedPath(IReadOnlyList<HelpTreeVisibleNode> visibleNodes, int selectedIndex)
    {
        if (visibleNodes.Count == 0 || selectedIndex < 0 || selectedIndex >= visibleNodes.Count)
        {
            return "<none>";
        }

        var segments = new Stack<string>();
        var currentIndex = selectedIndex;

        while (currentIndex >= 0 && currentIndex < visibleNodes.Count)
        {
            var current = visibleNodes[currentIndex];
            segments.Push(current.Kind == HelpTreeNodeKind.Topic ? current.Topic!.Name : current.Category);

            if (current.ParentIndex is not int parentIndex)
            {
                break;
            }

            currentIndex = parentIndex;
        }

        return string.Join(" > ", segments);
    }

    private static int GetHelpPageSize()
    {
        try
        {
            return Math.Clamp(Console.WindowHeight - 18, 8, 16);
        }
        catch
        {
            return 10;
        }
    }

    private static void WriteHelpSummary(
        HelpTreeState state,
        int visibleTopicCount,
        ToshTextStyleConfig titleStyle,
        ToshTextStyleConfig footerStyle)
    {
        var topic = state.SelectedTopic;
        var summary = topic is not null
            ? StyledText.RenderSegments(
            [
                titleStyle.Apply("help"),
                $": {topic.Name} ",
                footerStyle.Apply($"({topic.Kind} / {topic.Category}, {visibleTopicCount} visible)")
            ])
            : StyledText.RenderSegments(
            [
                titleStyle.Apply("help"),
                footerStyle.Apply($": browser closed ({visibleTopicCount} visible)")
            ]);

        Console.WriteLine(summary);
    }

    private static void DrainHelpFilterInput(
        HelpTreeState state,
        StringBuilder filterBuffer,
        int innerWidth,
        ToshTextStyleConfig borderStyle,
        ToshTextStyleConfig footerStyle,
        ref bool editingFilter,
        ref string? filterSnapshot,
        ref bool filterDirty)
    {
        if (!editingFilter)
        {
            return;
        }

        var deadline = Stopwatch.GetTimestamp() + HelpFilterBatchWindowTicks;

        while (editingFilter)
        {
            if (TryReadQueuedKey(out var queuedKey))
            {
                ProcessHelpFilterKey(state, filterBuffer, queuedKey, innerWidth, borderStyle, footerStyle, ref editingFilter, ref filterSnapshot, ref filterDirty);
                deadline = Stopwatch.GetTimestamp() + HelpFilterBatchWindowTicks;
                continue;
            }

            if (Stopwatch.GetTimestamp() >= deadline)
            {
                return;
            }

            Thread.Sleep(1);
        }
    }

    private static void ProcessHelpFilterKey(
        HelpTreeState state,
        StringBuilder filterBuffer,
        ConsoleKeyInfo key,
        int innerWidth,
        ToshTextStyleConfig borderStyle,
        ToshTextStyleConfig footerStyle,
        ref bool editingFilter,
        ref string? filterSnapshot,
        ref bool filterDirty)
    {
        switch (key.Key)
        {
            case ConsoleKey.Enter:
                editingFilter = false;
                filterSnapshot = null;
                return;
            case ConsoleKey.Escape:
                editingFilter = false;
                state.SetFilter(filterSnapshot ?? string.Empty);
                filterBuffer.Clear();
                filterBuffer.Append(state.Filter);
                filterSnapshot = null;
                filterDirty = false;
                return;
            case ConsoleKey.Backspace:
                if (filterBuffer.Length > 0)
                {
                    filterBuffer.Length--;
                    PreviewHelpFilterFooter(filterBuffer.ToString(), innerWidth, borderStyle, footerStyle);
                    filterDirty = true;
                }

                return;
            default:
                if (key.KeyChar >= ' ')
                {
                    filterBuffer.Append(key.KeyChar);
                    PreviewHelpFilterFooter(filterBuffer.ToString(), innerWidth, borderStyle, footerStyle);
                    filterDirty = true;
                }

                return;
        }
    }

    private static void PreviewHelpFilterFooter(
        string filterText,
        int innerWidth,
        ToshTextStyleConfig borderStyle,
        ToshTextStyleConfig footerStyle)
    {
        var footerText = $"/ filter: {filterText}  enter apply  esc cancel";

        Console.Write("\r");
        MoveUp(1);
        Console.Write(ClearLine);
        Console.Write(BuildInspectContentRow(footerText, innerWidth, borderStyle, footerStyle));
        Console.Write("\r");
        Console.Write("\x1b[1B");
    }

    private static bool TryReadQueuedKey(out ConsoleKeyInfo key)
    {
        try
        {
            if (Console.KeyAvailable)
            {
                key = Console.ReadKey(intercept: true);
                return true;
            }
        }
        catch
        {
        }

        key = default;
        return false;
    }
}
