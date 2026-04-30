using System.Text;
using Tosh.Runtime;

namespace Tosh.Cli;

internal sealed partial class ConsoleInlinePromptProvider
{
    public void Inspect(object? value, bool includeAllMembers = false, string? sourceExpression = null)
    {
        if (_formatter is null)
        {
            return;
        }

        var state = new InspectTreeState(new ObjectTreeBuilder(_formatter), value, includeAllMembers, sourceExpression);
        RunInspectLoop(state, includeAllMembers);
    }

    private void RunInspectLoop(InspectTreeState state, bool includeAllMembers)
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
        var memberNameStyle = new ToshTextStyleConfig(bold: true);
        var typeNameStyle = new ToshTextStyleConfig(foreground: "magenta");
        var stringValueStyle = new ToshTextStyleConfig(foreground: "green");
        var numberValueStyle = new ToshTextStyleConfig(foreground: "cyan");
        var boolValueStyle = new ToshTextStyleConfig(foreground: "yellow");
        var nullValueStyle = new ToshTextStyleConfig(foreground: "red", dim: true);

        var pageSize = GetInspectPageSize();
        var totalLines = pageSize + 11;
        var viewOffset = 0;
        var firstRender = true;
        var editingFilter = false;
        var filterBuffer = new StringBuilder(state.Filter);
        string? filterSnapshot = null;

        Console.Write(HideCursor);
        Console.Write(EnableSgrMouse);

        try
        {
            while (true)
            {
                var frame = state.CurrentFrame;
                var innerWidth = Math.Max(40, (TryGetConsoleWidth() ?? 100) - 2);
                var title = $" inspect: {frame.TypeName} ";
                var visibleNodes = state.VisibleNodes;
                var selectedIndex = state.SelectedIndex;

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

                Console.Write(ClearLine);
                Console.WriteLine(BuildInspectBorderLine(box.TopLeft, box.TopRight, box.Horizontal, title, innerWidth, borderStyle, titleStyle));

                Console.Write(ClearLine);
                Console.WriteLine(BuildInspectStyledContentRow(
                    BuildInspectHeaderSegments("assembly", frame.AssemblyName ?? "<unknown>", memberNameStyle, typeNameStyle),
                    innerWidth,
                    borderStyle));

                Console.Write(ClearLine);
                Console.WriteLine(BuildInspectStyledContentRow(
                    BuildInspectHeaderSegments("base", frame.BaseTypeName ?? "<none>", memberNameStyle, typeNameStyle),
                    innerWidth,
                    borderStyle));

                Console.Write(ClearLine);
                Console.WriteLine(BuildInspectStyledContentRow(
                    BuildInspectHeaderSegments(
                        "value",
                        frame.ValuePreview,
                        memberNameStyle,
                        GetValueStyle(frame.RootValue?.GetType(), frame.TypeName, frame.ValuePreview, textStyle, stringValueStyle, numberValueStyle, boolValueStyle, nullValueStyle)),
                    innerWidth,
                    borderStyle));

                Console.Write(ClearLine);
                Console.WriteLine(BuildInspectStyledContentRow(
                    BuildInspectPathSegments(frame.Breadcrumb, memberNameStyle, footerStyle),
                    innerWidth,
                    borderStyle));

                Console.Write(ClearLine);
                Console.WriteLine(borderStyle.Apply($"{box.MiddleLeft}{new string(box.Horizontal, innerWidth)}{box.MiddleRight}").ToAnsi());

                for (var row = 0; row < pageSize; row++)
                {
                    Console.Write(ClearLine);

                    var visibleIndex = viewOffset + row;
                    string line;

                    if (visibleNodes.Count == 0)
                    {
                        line = row == 0
                            ? BuildInspectContentRow("<no matches>", innerWidth, borderStyle, new ToshTextStyleConfig(foreground: "red", dim: true))
                            : BuildInspectContentRow(string.Empty, innerWidth, borderStyle, footerStyle);
                    }
                    else if (visibleIndex < visibleNodes.Count)
                    {
                        line = BuildInspectNodeRow(
                            visibleNodes[visibleIndex],
                            visibleIndex == selectedIndex,
                            innerWidth,
                            borderStyle,
                            sectionStyle,
                            selectionStyle,
                            footerStyle,
                            textStyle,
                            memberNameStyle,
                            typeNameStyle,
                            stringValueStyle,
                            numberValueStyle,
                            boolValueStyle,
                            nullValueStyle);
                    }
                    else
                    {
                        line = BuildInspectContentRow(string.Empty, innerWidth, borderStyle, footerStyle);
                    }

                    Console.WriteLine(line);
                }

                Console.Write(ClearLine);
                Console.WriteLine(borderStyle.Apply($"{box.MiddleLeft}{new string(box.Horizontal, innerWidth)}{box.MiddleRight}").ToAnsi());

                var selectedNode = state.SelectedNode;
                Console.Write(ClearLine);
                Console.WriteLine(BuildInspectStyledContentRow(
                    BuildSelectedDetailSegments(
                        selectedNode?.Node,
                        textStyle,
                        memberNameStyle,
                        typeNameStyle,
                        stringValueStyle,
                        numberValueStyle,
                        boolValueStyle,
                        nullValueStyle),
                    innerWidth,
                    borderStyle));

                var statusText = BuildInspectStatusText(state, visibleNodes, selectedIndex);
                Console.Write(ClearLine);
                Console.WriteLine(BuildInspectContentRow(statusText, innerWidth, borderStyle, footerStyle));

                var footerText = editingFilter
                    ? $"/ filter: {filterBuffer}  enter apply  esc cancel"
                    : $" ↑/↓ navigate  ←/→ collapse/expand  tab inspect child  i insert  / filter  q quit {(includeAllMembers ? " [all]" : string.Empty)}";
                Console.Write(ClearLine);
                Console.WriteLine(BuildInspectContentRow(footerText, innerWidth, borderStyle, footerStyle));

                Console.Write(ClearLine);
                Console.Write(borderStyle.Apply($"{box.BottomLeft}{new string(box.Horizontal, innerWidth)}{box.BottomRight}").ToAnsi());

                var input = _inputReader.Read();

                if (input.IsMouse)
                {
                    var mouse = input.Mouse;
                    const int inspectHeaderLines = 6;

                    if (mouse.Action == Tui.TuiMouseAction.Scroll)
                    {
                        if (mouse.Button == Tui.TuiMouseButton.ScrollUp)
                            state.MoveUp();
                        else if (mouse.Button == Tui.TuiMouseButton.ScrollDown)
                            state.MoveDown();
                    }
                    else if (mouse.Action == Tui.TuiMouseAction.Press && mouse.Button == Tui.TuiMouseButton.Left)
                    {
                        var (_, bottomRow) = Console.GetCursorPosition();
                        var listStartRow = bottomRow - totalLines + 1 + inspectHeaderLines;
                        var listRow = mouse.Row - listStartRow;
                        if (listRow >= 0 && listRow < pageSize)
                        {
                            var clickedIndex = viewOffset + listRow;
                            if (clickedIndex < visibleNodes.Count)
                            {
                                state.SelectIndex(clickedIndex);
                            }
                        }
                    }

                    continue;
                }

                if (!input.IsKey)
                {
                    continue;
                }

                var key = input.Key;

                if (!editingFilter && key.KeyChar == '/')
                {
                    editingFilter = true;
                    filterSnapshot = state.Filter;
                    filterBuffer.Clear();
                    filterBuffer.Append(state.Filter);
                    continue;
                }

                if (editingFilter)
                {
                    switch (key.Key)
                    {
                        case ConsoleKey.Enter:
                            editingFilter = false;
                            filterSnapshot = null;
                            break;
                        case ConsoleKey.Escape:
                            editingFilter = false;
                            state.SetFilter(filterSnapshot ?? string.Empty);
                            filterBuffer.Clear();
                            filterBuffer.Append(state.Filter);
                            filterSnapshot = null;
                            break;
                        case ConsoleKey.Backspace:
                            if (filterBuffer.Length > 0)
                            {
                                filterBuffer.Length--;
                                state.SetFilter(filterBuffer.ToString());
                            }

                            break;
                        default:
                            if (key.KeyChar >= ' ')
                            {
                                filterBuffer.Append(key.KeyChar);
                                state.SetFilter(filterBuffer.ToString());
                            }

                            break;
                    }

                    continue;
                }

                switch (key.Key)
                {
                    case ConsoleKey.Insert or ConsoleKey.I:
                        if (TryInsertInspectSelection(state))
                        {
                            CleanupLines(totalLines);
                            WriteInspectSummary(frame, includeAllMembers, visibleNodes.Count, titleStyle, footerStyle);
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
                    case ConsoleKey.Tab:
                        state.DrillIntoSelected();
                        viewOffset = 0;
                        break;
                    case ConsoleKey.Oem2:
                        break;
                    case ConsoleKey.Escape or ConsoleKey.Q:
                        CleanupLines(totalLines);
                        WriteInspectSummary(frame, includeAllMembers, visibleNodes.Count, titleStyle, footerStyle);
                        return;
                }
            }
        }
        finally
        {
            Console.Write(DisableSgrMouse);
            Console.Write(ShowCursor);
        }
    }

    private static string BuildInspectBorderLine(
        char left,
        char right,
        char horizontal,
        string title,
        int innerWidth,
        ToshTextStyleConfig borderStyle,
        ToshTextStyleConfig titleStyle)
    {
        var titleVisible = Math.Min(innerWidth, title.Length);
        var clippedTitle = titleVisible < title.Length ? title[..Math.Max(1, titleVisible - 1)] + "…" : title;
        var trailing = Math.Max(0, innerWidth - clippedTitle.Length);

        return borderStyle.Apply($"{left}").ToAnsi()
               + titleStyle.Apply(clippedTitle).ToAnsi()
               + borderStyle.Apply($"{new string(horizontal, trailing)}{right}").ToAnsi();
    }

    private static string BuildInspectContentRow(
        string text,
        int innerWidth,
        ToshTextStyleConfig borderStyle,
        ToshTextStyleConfig? contentStyle)
    {
        var clipped = ClipPlainText(text, innerWidth);
        var padded = InlineTablePlan.PadRight(clipped, innerWidth);
        var content = contentStyle is null ? padded : contentStyle.Apply(padded).ToAnsi();
        var v = borderStyle.Apply("│").ToAnsi();
        return $"{v}{content}{v}";
    }

    private static string BuildInspectStyledContentRow(
        IReadOnlyList<(string Text, ToshTextStyleConfig Style)> segments,
        int innerWidth,
        ToshTextStyleConfig borderStyle)
    {
        var content = RenderInspectStyledSegments(segments, innerWidth);
        var v = borderStyle.Apply("│").ToAnsi();
        return $"{v}{content}{v}";
    }

    private static string BuildInspectNodeRow(
        InspectVisibleNode visibleNode,
        bool selected,
        int innerWidth,
        ToshTextStyleConfig borderStyle,
        ToshTextStyleConfig sectionStyle,
        ToshTextStyleConfig selectionStyle,
        ToshTextStyleConfig footerStyle,
        ToshTextStyleConfig textStyle,
        ToshTextStyleConfig memberNameStyle,
        ToshTextStyleConfig typeNameStyle,
        ToshTextStyleConfig stringValueStyle,
        ToshTextStyleConfig numberValueStyle,
        ToshTextStyleConfig boolValueStyle,
        ToshTextStyleConfig nullValueStyle)
    {
        var node = visibleNode.Node;

        if (node.Kind == InspectTreeNodeKind.Section)
        {
            var text = BuildSectionText(node, visibleNode.Depth);
            var clipped = ClipPlainText(text, innerWidth);
            var padded = InlineTablePlan.PadRight(clipped, innerWidth);
            var sectionContent = (selected ? ApplySelectionOverlay(sectionStyle, selectionStyle) : sectionStyle).Apply(padded).ToAnsi();
            var sectionBorder = borderStyle.Apply("│").ToAnsi();
            return $"{sectionBorder}{sectionContent}{sectionBorder}";
        }

        if (node.Kind is InspectTreeNodeKind.Message or InspectTreeNodeKind.Ellipsis)
        {
            var text = $"{new string(' ', visibleNode.Depth * 2)}{node.Text}";
            var clipped = ClipPlainText(text, innerWidth);
            var padded = InlineTablePlan.PadRight(clipped, innerWidth);
            var messageStyle = selected ? ApplySelectionOverlay(footerStyle, selectionStyle) : footerStyle;
            var messageContent = messageStyle.Apply(padded).ToAnsi();
            var messageBorder = borderStyle.Apply("│").ToAnsi();
            return $"{messageBorder}{messageContent}{messageBorder}";
        }

        var segments = node.Kind == InspectTreeNodeKind.Method
            ? BuildInspectMethodSegments(node, visibleNode.Depth, textStyle, memberNameStyle, typeNameStyle, footerStyle)
            : BuildInspectMemberSegments(node, visibleNode.Depth, textStyle, memberNameStyle, typeNameStyle, footerStyle, stringValueStyle, numberValueStyle, boolValueStyle, nullValueStyle);
        if (selected)
        {
            segments = segments
                .Select(segment => (segment.Text, ApplySelectionOverlay(segment.Style, selectionStyle)))
                .ToArray();
        }

        var content = RenderInspectStyledSegments(segments, innerWidth);
        var v = borderStyle.Apply("│").ToAnsi();
        return $"{v}{content}{v}";
    }

    private static string BuildSectionText(InspectTreeNode node, int depth)
    {
        var indent = new string(' ', depth * 2);
        var glyph = node.IsExpanded ? "▼" : "▶";
        var count = node.Count is int value ? $" ({value})" : string.Empty;
        return $"{indent}{glyph} {node.Text}{count}";
    }

    private static IReadOnlyList<(string Text, ToshTextStyleConfig Style)> BuildInspectHeaderSegments(
        string label,
        string value,
        ToshTextStyleConfig labelStyle,
        ToshTextStyleConfig valueStyle)
    {
        return
        [
            ($" {label}: ", labelStyle),
            (value, valueStyle),
        ];
    }

    private static IReadOnlyList<(string Text, ToshTextStyleConfig Style)> BuildInspectPathSegments(
        IReadOnlyList<string> breadcrumb,
        ToshTextStyleConfig labelStyle,
        ToshTextStyleConfig separatorStyle)
    {
        var segments = new List<(string Text, ToshTextStyleConfig Style)>
        {
            (" path: ", labelStyle),
        };

        for (var index = 0; index < breadcrumb.Count; index += 1)
        {
            if (index > 0)
            {
                segments.Add((" > ", separatorStyle));
            }

            segments.Add((breadcrumb[index], labelStyle));
        }

        return segments;
    }

    private static IReadOnlyList<(string Text, ToshTextStyleConfig Style)> BuildInspectMemberSegments(
        InspectTreeNode node,
        int depth,
        ToshTextStyleConfig textStyle,
        ToshTextStyleConfig memberNameStyle,
        ToshTextStyleConfig typeNameStyle,
        ToshTextStyleConfig separatorStyle,
        ToshTextStyleConfig stringValueStyle,
        ToshTextStyleConfig numberValueStyle,
        ToshTextStyleConfig boolValueStyle,
        ToshTextStyleConfig nullValueStyle)
    {
        var segments = new List<(string Text, ToshTextStyleConfig Style)>
        {
            (new string(' ', depth * 2), textStyle),
            ($"{(node.HasChildren ? (node.IsExpanded ? "▼" : "▶") : " ")} ", separatorStyle),
            (node.Text, memberNameStyle),
        };

        if (!string.IsNullOrWhiteSpace(node.TypeName))
        {
            segments.Add((" : ", separatorStyle));
            segments.Add((node.TypeName!, typeNameStyle));
        }

        if (!string.IsNullOrWhiteSpace(node.ValuePreview))
        {
            segments.Add((" = ", separatorStyle));
            segments.Add((node.ValuePreview!, GetValueStyle(null, node.TypeName, node.ValuePreview, textStyle, stringValueStyle, numberValueStyle, boolValueStyle, nullValueStyle)));
        }

        return segments;
    }

    private static IReadOnlyList<(string Text, ToshTextStyleConfig Style)> BuildInspectMethodSegments(
        InspectTreeNode node,
        int depth,
        ToshTextStyleConfig textStyle,
        ToshTextStyleConfig memberNameStyle,
        ToshTextStyleConfig typeNameStyle,
        ToshTextStyleConfig separatorStyle)
    {
        var signature = node.Text;
        var parenIndex = signature.IndexOf('(');
        var returnIndex = signature.LastIndexOf(" -> ", StringComparison.Ordinal);
        var methodName = parenIndex >= 0 ? signature[..parenIndex] : signature;
        var remainderStart = parenIndex >= 0 ? parenIndex : signature.Length;
        var parameters = returnIndex >= 0
            ? signature[remainderStart..returnIndex]
            : signature[remainderStart..];

        var segments = new List<(string Text, ToshTextStyleConfig Style)>
        {
            (new string(' ', depth * 2), textStyle),
            ("  ", separatorStyle),
            (methodName, memberNameStyle),
            (parameters, textStyle),
        };

        if (returnIndex >= 0)
        {
            segments.Add((" -> ", separatorStyle));
            segments.Add((signature[(returnIndex + 4)..], typeNameStyle));
        }

        return segments;
    }

    private static IReadOnlyList<(string Text, ToshTextStyleConfig Style)> BuildSelectedDetailSegments(
        InspectTreeNode? node,
        ToshTextStyleConfig textStyle,
        ToshTextStyleConfig memberNameStyle,
        ToshTextStyleConfig typeNameStyle,
        ToshTextStyleConfig stringValueStyle,
        ToshTextStyleConfig numberValueStyle,
        ToshTextStyleConfig boolValueStyle,
        ToshTextStyleConfig nullValueStyle)
    {
        var segments = new List<(string Text, ToshTextStyleConfig Style)>
        {
            (" selected: ", memberNameStyle),
        };

        if (node is null)
        {
            segments.Add(("<none>", nullValueStyle));
            return segments;
        }

        if (node.Kind == InspectTreeNodeKind.Section)
        {
            segments.Add((node.Text, memberNameStyle));
            if (node.Count is int value)
            {
                segments.Add(($" ({value})", textStyle));
            }

            return segments;
        }

        if (node.Kind == InspectTreeNodeKind.Method)
        {
            return segments
                .Concat(BuildInspectMethodSegments(node, depth: 0, textStyle, memberNameStyle, typeNameStyle, textStyle).Skip(2))
                .ToArray();
        }

        return segments
            .Concat(BuildInspectMemberSegments(node, depth: 0, textStyle, memberNameStyle, typeNameStyle, textStyle, stringValueStyle, numberValueStyle, boolValueStyle, nullValueStyle).Skip(2))
            .ToArray();
    }

    private static string BuildInspectStatusText(
        InspectTreeState state,
        IReadOnlyList<InspectVisibleNode> visibleNodes,
        int selectedIndex)
    {
        var position = visibleNodes.Count == 0 ? "0/0" : $"{selectedIndex + 1}/{visibleNodes.Count}";
        var depth = state.SelectedNode?.Depth ?? 0;
        var path = BuildSelectedPath(visibleNodes, selectedIndex);
        var backHint = state.CanNavigateBack ? "  ← back to parent object" : string.Empty;
        var filterHint = string.IsNullOrWhiteSpace(state.Filter) ? string.Empty : $"  filter: {state.Filter}";

        return $" status: {position}  depth {depth}  path {path}{backHint}{filterHint}";
    }

    private static string BuildSelectedPath(IReadOnlyList<InspectVisibleNode> visibleNodes, int selectedIndex)
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
            segments.Push(current.Node.Text);

            if (current.ParentIndex is not int parentIndex)
            {
                break;
            }

            currentIndex = parentIndex;
        }

        return string.Join(" > ", segments);
    }

    private static string RenderInspectStyledSegments(
        IReadOnlyList<(string Text, ToshTextStyleConfig Style)> segments,
        int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        var plainLength = segments.Sum(segment => segment.Text.Length);
        var needsEllipsis = plainLength > width;
        var remaining = needsEllipsis ? Math.Max(0, width - 1) : width;
        var builder = new StringBuilder();
        ToshTextStyleConfig? ellipsisStyle = null;

        foreach (var (text, style) in segments)
        {
            if (remaining <= 0)
            {
                if (text.Length > 0)
                {
                    ellipsisStyle ??= style;
                }

                break;
            }

            if (text.Length <= remaining)
            {
                AppendStyledText(builder, text, style);
                remaining -= text.Length;
                ellipsisStyle = style;
                continue;
            }

            AppendStyledText(builder, text[..remaining], style);
            remaining = 0;
            ellipsisStyle = style;
            break;
        }

        if (needsEllipsis)
        {
            AppendStyledText(builder, "…", ellipsisStyle ?? new ToshTextStyleConfig());
        }
        else if (remaining > 0)
        {
            builder.Append(' ', remaining);
        }

        return builder.ToString();
    }

    private static void AppendStyledText(StringBuilder builder, string text, ToshTextStyleConfig style)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        builder.Append(style.Apply(text).ToAnsi());
    }

    private static ToshTextStyleConfig ApplySelectionOverlay(ToshTextStyleConfig baseStyle, ToshTextStyleConfig selectionStyle)
    {
        return new ToshTextStyleConfig(
            foreground: baseStyle.Foreground ?? selectionStyle.Foreground,
            background: selectionStyle.Background ?? baseStyle.Background,
            bold: baseStyle.Bold || selectionStyle.Bold,
            italic: baseStyle.Italic || selectionStyle.Italic,
            underline: baseStyle.Underline || selectionStyle.Underline,
            dim: baseStyle.Dim);
    }

    private static ToshTextStyleConfig GetValueStyle(
        Type? runtimeType,
        string? typeName,
        string? preview,
        ToshTextStyleConfig defaultStyle,
        ToshTextStyleConfig stringValueStyle,
        ToshTextStyleConfig numberValueStyle,
        ToshTextStyleConfig boolValueStyle,
        ToshTextStyleConfig nullValueStyle)
    {
        if (runtimeType is not null)
        {
            runtimeType = Nullable.GetUnderlyingType(runtimeType) ?? runtimeType;

            if (runtimeType == typeof(string) || runtimeType == typeof(char))
            {
                return stringValueStyle;
            }

            if (runtimeType == typeof(bool))
            {
                return boolValueStyle;
            }

            if (IsNumericRuntimeType(runtimeType))
            {
                return numberValueStyle;
            }
        }

        if (string.Equals(preview, "null", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(typeName, "null", StringComparison.OrdinalIgnoreCase))
        {
            return nullValueStyle;
        }

        if (IsStringLikeTypeName(typeName))
        {
            return stringValueStyle;
        }

        if (IsBooleanLikeTypeName(typeName))
        {
            return boolValueStyle;
        }

        if (IsNumericLikeTypeName(typeName))
        {
            return numberValueStyle;
        }

        return defaultStyle;
    }

    private bool TryInsertInspectSelection(InspectTreeState state)
    {
        var sink = _runtime?.CommandLineInsertion;
        var text = BuildInspectInsertionText(state);
        return sink is not null &&
               !string.IsNullOrWhiteSpace(text) &&
               sink.TryInsertText(text);
    }

    internal static string? BuildInspectInsertionText(InspectTreeState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var text = state.GetSelectedInsertionText();

        if (!string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var node = state.SelectedNode?.Node;
        return node?.Kind == InspectTreeNodeKind.Interface ? node.Text : null;
    }

    private static bool IsStringLikeTypeName(string? typeName)
    {
        return string.Equals(typeName, "String", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(typeName, "Char", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBooleanLikeTypeName(string? typeName)
    {
        return string.Equals(typeName, "Boolean", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(typeName, "bool", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNumericLikeTypeName(string? typeName)
    {
        return typeName switch
        {
            "Byte" or "SByte" or "Int16" or "UInt16" or "Int32" or "UInt32" or "Int64" or "UInt64" or
            "Int128" or "UInt128" or "Single" or "Double" or "Half" or "Decimal" or "BigInteger" or
            "nint" or "nuint" => true,
            _ => false,
        };
    }

    private static bool IsNumericRuntimeType(Type runtimeType)
    {
        if (runtimeType == typeof(System.Numerics.BigInteger))
        {
            return true;
        }

        return Type.GetTypeCode(runtimeType) switch
        {
            TypeCode.Byte or
            TypeCode.SByte or
            TypeCode.Int16 or
            TypeCode.UInt16 or
            TypeCode.Int32 or
            TypeCode.UInt32 or
            TypeCode.Int64 or
            TypeCode.UInt64 or
            TypeCode.Single or
            TypeCode.Double or
            TypeCode.Decimal => true,
            _ => false,
        };
    }

    private static string ClipPlainText(string text, int width)
    {
        if (width <= 0)
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

        return text[..(width - 1)] + "…";
    }

    private static int GetInspectPageSize()
    {
        try
        {
            return Math.Clamp(Console.WindowHeight - 14, 8, 18);
        }
        catch
        {
            return 12;
        }
    }

    private static void WriteInspectSummary(
        InspectTreeFrame frame,
        bool includeAllMembers,
        int visibleCount,
        ToshTextStyleConfig titleStyle,
        ToshTextStyleConfig footerStyle)
    {
        var membersLabel = frame.SummaryMemberCount == 1 ? "member" : "members";
        var visibility = visibleCount > 0 ? $", {visibleCount} visible" : string.Empty;
        var suffix = includeAllMembers ? ", all members" : string.Empty;
        var summary = StyledText.RenderSegments(
        [
            titleStyle.Apply("inspect"),
            $": {frame.TypeName} ",
            footerStyle.Apply($"({frame.SummaryMemberCount} {membersLabel}{visibility}{suffix})")
        ]);

        Console.WriteLine(summary);
    }
}
