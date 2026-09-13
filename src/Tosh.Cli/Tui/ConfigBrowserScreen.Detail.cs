using System.Globalization;
using System.Text;
using Tosh.Runtime;
using Tosh.Cli;
using Tosh.Tui.Requests;
using Tosh.Tui;

namespace Tosh.Cli.Tui;

/// <summary>
/// The config browser's detail pane: what the right-hand side draws for a node.
/// </summary>
/// <remarks>
/// Split out of <c>ConfigBrowserScreen.cs</c>, which had grown to 3,519 lines. This half
/// only reads: it renders values, validation, staged diffs, and the live theme and prompt
/// previews. Everything that changes a value lives in
/// <c>ConfigBrowserScreen.Editors.cs</c> alongside it.
/// </remarks>
internal sealed partial class ConfigBrowserScreen
{
    internal IReadOnlyList<string> BuildDetailLines(int width)
    {
        return BuildDetailEntries(width)
            .Select(entry => entry.Text)
            .ToArray();
    }

    private IReadOnlyList<ConfigDetailEntry> BuildDetailEntries(int width)
    {
        if (!_tree.TryGetSelected(out var selected))
        {
            return [new ConfigDetailEntry("Select a config node from the tree.", ConfigDetailEntryKind.Meta)];
        }

        if (_confirmDialog.IsOpen)
        {
            return
            [
                new ConfigDetailEntry("Confirmation", ConfigDetailEntryKind.SectionHeading),
                new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body),
                .. _confirmDialog.BuildEntries(width).Select(text => new ConfigDetailEntry(text, ConfigDetailEntryKind.Body)),
            ];
        }

        var lines = new List<ConfigDetailEntry>();
        var node = selected.Node;
        var currentValue = GetCurrentValue(node);
        var effectiveValue = GetEffectiveValue(node);
        var defaultValue = GetDefaultValue(node);
        var hasStagedValue = node.Path.Length > 0 && _stagedValues.ContainsKey(node.Path);
        var stagedCount = CountStagedChanges(node);
        var validationMessages = GetValidationMessages(node).ToArray();
        var shellPath = node.Path.Length == 0 ? "$tosh.Config" : $"$tosh.Config.{node.Path}";

        lines.Add(new ConfigDetailEntry(node.Kind == ConfigBrowserNodeKind.Group ? "Config Section" : "Config Value", ConfigDetailEntryKind.SectionHeading));
        lines.Add(new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body));
        lines.Add(new ConfigDetailEntry($"Path: {shellPath}", ConfigDetailEntryKind.Meta));
        lines.Add(new ConfigDetailEntry($"Type: {node.TypeName}", ConfigDetailEntryKind.Meta));
        lines.Add(new ConfigDetailEntry($"Kind: {FormatEditorKind(node.EditorKind)}", ConfigDetailEntryKind.Meta));
        lines.Add(new ConfigDetailEntry($"Nullable: {TuiRenderHelpers.FormatBoolean(node.IsNullable)}  Editable: {TuiRenderHelpers.FormatBoolean(node.IsEditable)}  Resettable: {TuiRenderHelpers.FormatBoolean(node.IsResettable)}", ConfigDetailEntryKind.Meta));

        if (stagedCount > 0)
        {
            lines.Add(new ConfigDetailEntry($"Staged Changes: {stagedCount}", ConfigDetailEntryKind.Meta));
        }

        if (!string.IsNullOrWhiteSpace(_statusMessage))
        {
            lines.Add(new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body));
            lines.Add(new ConfigDetailEntry("Status Message", ConfigDetailEntryKind.SectionHeading));
            lines.AddRange(TextDocumentFormatter.WrapParagraph(_statusMessage, width)
                .Select(text => new ConfigDetailEntry(text, ConfigDetailEntryKind.Meta)));
        }

        if (stagedCount > 0 && (node.Kind == ConfigBrowserNodeKind.Group || stagedCount > 1))
        {
            lines.Add(new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body));
            lines.Add(new ConfigDetailEntry("Staged Diff", ConfigDetailEntryKind.SectionHeading));
            lines.AddRange(BuildStagedDiffEntries(node, width));
        }

        if (node.Kind == ConfigBrowserNodeKind.Group)
        {
            var editableChildren = GetGroupEditableChildren(node);
            lines.Add(new ConfigDetailEntry($"Children: {node.Children.Count}", ConfigDetailEntryKind.Meta));

            if (node.ValueType == typeof(ToshTextStyleConfig))
            {
                lines.Add(new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body));
                lines.Add(new ConfigDetailEntry("Style Preview", ConfigDetailEntryKind.SectionHeading));
                lines.AddRange(BuildStylePreviewEntries(node));
            }

            if (ShouldShowPromptPreview(node))
            {
                lines.Add(new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body));
                lines.Add(new ConfigDetailEntry("Prompt Preview", ConfigDetailEntryKind.SectionHeading));
                lines.AddRange(BuildPromptPreviewEntries(width));
            }

            if (validationMessages.Length > 0)
            {
                lines.Add(new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body));
                lines.Add(new ConfigDetailEntry("Validation", ConfigDetailEntryKind.SectionHeading));
                lines.AddRange(BuildValidationEntries(validationMessages, width));
            }

            if (ShouldShowThemePreview(node))
            {
                lines.Add(new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body));
                lines.Add(new ConfigDetailEntry("Theme Preview", ConfigDetailEntryKind.SectionHeading));
                lines.AddRange(BuildThemePreviewEntries(node, width));
            }

            if (ShouldShowStartupActions(node))
            {
                lines.Add(new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body));
                lines.Add(new ConfigDetailEntry("Startup Actions", ConfigDetailEntryKind.SectionHeading));
                lines.AddRange(BuildStartupActionEntries(width));
            }

            if (editableChildren.Count > 0)
            {
                lines.Add(new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body));
                lines.Add(new ConfigDetailEntry(GetGroupEditorHeading(node), ConfigDetailEntryKind.SectionHeading));
                lines.AddRange(BuildGroupEditorEntries(node, width));
            }

            var activeChildEditorNode = GetActiveGroupChildEditorNode(node);

            if (activeChildEditorNode is not null)
            {
                lines.Add(new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body));
                lines.Add(new ConfigDetailEntry($"Editing {activeChildEditorNode.DisplayName}", ConfigDetailEntryKind.SectionHeading));
                lines.AddRange(BuildValueEditorEntries(activeChildEditorNode, width));
            }

            lines.Add(new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body));
            lines.Add(new ConfigDetailEntry("Members", ConfigDetailEntryKind.SectionHeading));

            foreach (var child in node.Children)
            {
                lines.Add(new ConfigDetailEntry(
                    $"  {child.DisplayName}{(HasStagedChanges(child) ? " *" : string.Empty)} [{FormatEditorKind(child.EditorKind)}] ({child.TypeName})",
                    ConfigDetailEntryKind.Body));
            }

            lines.Add(new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body));
            lines.AddRange(TextDocumentFormatter.WrapParagraph(
                    "Use Enter to expand or collapse sections in the tree. Press Shift+R to stage this section back to defaults, r to drop staged edits in this subtree, and a to apply all staged changes.",
                    width)
                .Select(text => new ConfigDetailEntry(text, ConfigDetailEntryKind.Meta)));

            return lines;
        }

        var status = ValuesEqual(currentValue, defaultValue) ? "default" : "customized";
        lines.Add(new ConfigDetailEntry($"Status: {status}{(hasStagedValue ? "  Pending: yes" : string.Empty)}", ConfigDetailEntryKind.Meta));
        lines.Add(new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body));

        if (hasStagedValue)
        {
            lines.Add(new ConfigDetailEntry("Staged Value", ConfigDetailEntryKind.SectionHeading));
            lines.AddRange(FormatValueBlock(effectiveValue, width));
            lines.Add(new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body));
        }

        lines.Add(new ConfigDetailEntry("Current Value", ConfigDetailEntryKind.SectionHeading));
        lines.AddRange(FormatValueBlock(currentValue, width));
        lines.Add(new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body));
        lines.Add(new ConfigDetailEntry("Default Value", ConfigDetailEntryKind.SectionHeading));
        lines.AddRange(FormatValueBlock(defaultValue, width));

        if (ShouldShowPromptPreview(node))
        {
            lines.Add(new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body));
            lines.Add(new ConfigDetailEntry("Prompt Preview", ConfigDetailEntryKind.SectionHeading));
            lines.AddRange(BuildPromptPreviewEntries(width));
        }

        if (IsPromptLayoutNode(node))
        {
            lines.Add(new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body));
            lines.Add(new ConfigDetailEntry("Prompt Layout Editor", ConfigDetailEntryKind.SectionHeading));
            lines.AddRange(BuildPromptLayoutEditorEntries(node, width));
        }

        if (validationMessages.Length > 0)
        {
            lines.Add(new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body));
            lines.Add(new ConfigDetailEntry("Validation", ConfigDetailEntryKind.SectionHeading));
            lines.AddRange(BuildValidationEntries(validationMessages, width));
        }

        if (ShouldShowThemePreview(node))
        {
            lines.Add(new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body));
            lines.Add(new ConfigDetailEntry("Theme Preview", ConfigDetailEntryKind.SectionHeading));
            lines.AddRange(BuildThemePreviewEntries(node, width));
        }

        if (node.EditorKind == ConfigBrowserEditorKind.Collection)
        {
            lines.Add(new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body));
            lines.Add(new ConfigDetailEntry("Collection View", ConfigDetailEntryKind.SectionHeading));
            lines.AddRange(BuildCollectionEntries(node, width));
        }

        if (ShouldShowStartupActions(node))
        {
            lines.Add(new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body));
            lines.Add(new ConfigDetailEntry("Startup Actions", ConfigDetailEntryKind.SectionHeading));
            lines.AddRange(BuildStartupActionEntries(width));
        }

        if (node.EditorKind == ConfigBrowserEditorKind.Enum)
        {
            lines.Add(new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body));
            lines.Add(new ConfigDetailEntry("Options", ConfigDetailEntryKind.SectionHeading));

            var enumNames = Enum.GetNames(node.ValueType);
            var isEditingThisEnum = _editMode == ConfigBrowserEditMode.Enum && string.Equals(_editingPath, node.Path, StringComparison.OrdinalIgnoreCase);

            if (isEditingThisEnum)
            {
                _enumPicker.Refresh(enumNames, Math.Max(1, _detailLines.Bounds.Height), _enumPicker.SelectedKey);
            }

            for (var index = 0; index < enumNames.Length; index++)
            {
                var enumName = enumNames[index];
                var isEffective = string.Equals(effectiveValue?.ToString(), enumName, StringComparison.Ordinal);
                var isCurrent = string.Equals(currentValue?.ToString(), enumName, StringComparison.Ordinal);
                var prefix = isEditingThisEnum
                    ? (index == _enumPicker.SelectedIndex ? ">" : " ")
                    : " ";
                var suffix = isCurrent && !isEffective ? " [live]" : string.Empty;
                lines.Add(new ConfigDetailEntry($"{prefix} {(isEffective ? "(*)" : "( )")} {enumName}{suffix}", ConfigDetailEntryKind.Body));
            }

            lines.Add(new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body));
            lines.AddRange(TextDocumentFormatter.WrapParagraph(
                    isEditingThisEnum
                        ? "Up and Down move through enum options. Press Enter to stage the selected value, or Esc to cancel."
                        : "Press e to edit this enum value.",
                    width)
                .Select(text => new ConfigDetailEntry(text, ConfigDetailEntryKind.Meta)));
        }
        else if (node.EditorKind == ConfigBrowserEditorKind.Boolean)
        {
            lines.Add(new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body));
            lines.Add(new ConfigDetailEntry("Control", ConfigDetailEntryKind.SectionHeading));
            lines.Add(new ConfigDetailEntry((effectiveValue is bool boolean && boolean) ? "[x] enabled" : "[ ] enabled", ConfigDetailEntryKind.Body));
            lines.Add(new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body));
            lines.AddRange(TextDocumentFormatter.WrapParagraph(
                    "Press Space to toggle this value, a to apply it, r to drop the staged change, or Shift+R to stage the default value.",
                    width)
                .Select(text => new ConfigDetailEntry(text, ConfigDetailEntryKind.Meta)));
        }
        else if ((_editMode == ConfigBrowserEditMode.Text ||
                  _editMode == ConfigBrowserEditMode.Path ||
                  _editMode == ConfigBrowserEditMode.Color) &&
                 string.Equals(_editingPath, node.Path, StringComparison.OrdinalIgnoreCase))
        {
            lines.Add(new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body));
            lines.Add(new ConfigDetailEntry("Editor", ConfigDetailEntryKind.SectionHeading));
            lines.AddRange(BuildValueEditorEntries(node, width));
        }
        else if (_editMode == ConfigBrowserEditMode.PromptLayout && string.Equals(_editingPath, node.Path, StringComparison.OrdinalIgnoreCase))
        {
            // The prompt layout editor section above is the active editor surface for this node.
        }
        else if (node.IsEditable)
        {
            lines.Add(new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body));
            lines.AddRange(TextDocumentFormatter.WrapParagraph(
                    "Press e to edit this value, a to apply staged changes, r to drop the staged value, or Shift+R to stage the default.",
                    width)
                .Select(text => new ConfigDetailEntry(text, ConfigDetailEntryKind.Meta)));
        }

        return lines;
    }

    private IReadOnlyList<ConfigDetailEntry> FormatValueBlock(object? value, int width)
    {
        var preview = FormatValuePreview(value);

        if (preview.Length == 0)
        {
            return [new ConfigDetailEntry("<empty>", ConfigDetailEntryKind.Body)];
        }

        return TextDocumentFormatter.WrapParagraph(preview, width)
            .Select(text => new ConfigDetailEntry(text, ConfigDetailEntryKind.Body))
            .ToArray();
    }

    private static string FormatValuePreview(object? value)
    {
        return value switch
        {
            null => "<null>",
            string text when text.Length == 0 => "\"\"",
            string text => $"\"{text.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)}\"",
            bool boolean => boolean ? "true" : "false",
            Enum => value.ToString() ?? string.Empty,
            System.Collections.IEnumerable enumerable when value is not string =>
                FormatEnumerablePreview(enumerable),
            _ => value.ToString() ?? string.Empty,
        };
    }

    private static string FormatEnumerablePreview(System.Collections.IEnumerable enumerable)
    {
        var items = enumerable.Cast<object?>()
            .Take(5)
            .Select(FormatValuePreview)
            .ToArray();
        var suffix = items.Length == 5 ? ", ..." : string.Empty;
        return $"[ {string.Join(", ", items)}{suffix} ]";
    }

    private IEnumerable<TuiValidationMessage> GetValidationMessages(ConfigBrowserNode node)
    {
        if (node.Kind == ConfigBrowserNodeKind.Group)
        {
            foreach (var child in node.Children)
            {
                foreach (var message in GetValidationMessages(child))
                {
                    yield return message with
                    {
                        Path = message.Path.Length == 0 ? child.DisplayName : message.Path
                    };
                }
            }

            yield break;
        }

        if (!node.IsEditable)
        {
            yield break;
        }

        var effectiveValue = GetEffectiveValue(node);

        if (IsColorConfigNode(node) && effectiveValue is string colorText && !StyledText.IsSupportedColor(colorText))
        {
            yield return new TuiValidationMessage(
                Path: node.DisplayName,
                Severity: TuiValidationSeverity.Error,
                Text: $"Color {FormatValuePreview(colorText)} is not a supported named or hex color.");
        }

        if (IsPromptLayoutNode(node) && effectiveValue is string layoutText)
        {
            foreach (var unknownModule in ToshPromptRenderer.GetUnknownLayoutModules(layoutText))
            {
                yield return new TuiValidationMessage(
                    Path: node.DisplayName,
                    Severity: TuiValidationSeverity.Warning,
                    Text: $"Prompt module {FormatValuePreview(unknownModule)} is not recognized.");
            }

            foreach (var module in ToshPromptRenderer.GetLayoutModules(layoutText))
            {
                var enabledPath = GetPromptModuleEnabledPath(module);

                if (enabledPath is null || !_schema.NodesByPath.TryGetValue(enabledPath, out var enabledNode))
                {
                    continue;
                }

                if (GetEffectiveValue(enabledNode) is bool enabled && enabled)
                {
                    continue;
                }

                yield return new TuiValidationMessage(
                    Path: node.DisplayName,
                    Severity: TuiValidationSeverity.Warning,
                    Text: $"Prompt module {FormatValuePreview(module)} is present in the layout but {enabledPath} is disabled.");
            }
        }
    }

    private static bool IsColorConfigNode(ConfigBrowserNode node)
    {
        return node.ValueType == typeof(string) &&
               (node.Name.Contains("Foreground", StringComparison.OrdinalIgnoreCase) ||
                node.Name.Contains("Background", StringComparison.OrdinalIgnoreCase) ||
                node.Name.EndsWith("Color", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPromptLayoutNode(ConfigBrowserNode node)
    {
        return node.ValueType == typeof(string) &&
               node.Path.StartsWith("Prompt.", StringComparison.OrdinalIgnoreCase) &&
               node.Name.EndsWith("Layout", StringComparison.OrdinalIgnoreCase);
    }

    private IReadOnlyList<ConfigDetailEntry> BuildStylePreviewEntries(ConfigBrowserNode node)
    {
        var foreground = GetChildValue<string>(node, "Foreground");
        var background = GetChildValue<string>(node, "Background");
        var bold = GetChildValue<bool>(node, "Bold");
        var italic = GetChildValue<bool>(node, "Italic");
        var underline = GetChildValue<bool>(node, "Underline");
        var dim = GetChildValue<bool>(node, "Dim");

        var previewForeground = StyledText.IsSupportedColor(foreground) ? foreground : null;
        var previewBackground = StyledText.IsSupportedColor(background) ? background : null;
        var attributeNames = new List<string>();

        if (bold)
        {
            attributeNames.Add("bold");
        }

        if (italic)
        {
            attributeNames.Add("italic");
        }

        if (underline)
        {
            attributeNames.Add("underline");
        }

        if (dim)
        {
            attributeNames.Add("dim");
        }

        var previewStyle = new ToshTextStyleConfig(previewForeground, previewBackground, bold, italic, underline, dim);

        return
        [
            new ConfigDetailEntry($"Foreground: {FormatValuePreview(foreground)}", ConfigDetailEntryKind.Body),
            new ConfigDetailEntry($"Background: {FormatValuePreview(background)}", ConfigDetailEntryKind.Body),
            new ConfigDetailEntry($"Attributes: {(attributeNames.Count == 0 ? "<none>" : string.Join(", ", attributeNames))}", ConfigDetailEntryKind.Body),
            new ConfigDetailEntry(previewStyle.Apply(" Sample Text 123 ").ToAnsi(), ConfigDetailEntryKind.Preview),
            new ConfigDetailEntry(previewStyle.Apply(" Heading / Accent Preview ").ToAnsi(), ConfigDetailEntryKind.Preview),
            new ConfigDetailEntry(previewStyle.Apply(" path/to/example  --flag  42 ").ToAnsi(), ConfigDetailEntryKind.Preview),
        ];
    }

    private IReadOnlyList<ConfigDetailEntry> BuildGroupEditorEntries(ConfigBrowserNode node, int width)
    {
        var editableChildren = GetGroupEditableChildren(node);

        if (editableChildren.Count == 0)
        {
            return
            [
                new ConfigDetailEntry("This style does not expose editable fields.", ConfigDetailEntryKind.Body),
            ];
        }

        var isEditingThisGroup = _editMode == ConfigBrowserEditMode.Group && string.Equals(_groupEditingPath, node.Path, StringComparison.OrdinalIgnoreCase);
        RefreshGroupEditor(node, _groupEditor.SelectedKey);
        var entries = new List<ConfigDetailEntry>(editableChildren.Count + 4);
        var visibleChildren = isEditingThisGroup
            ? _groupEditor.GetVisibleItems().Select(visible => (visible.Item, visible.IsSelected)).ToArray()
            : editableChildren.Select(child => (Item: child, IsSelected: false)).ToArray();

        foreach (var (child, isSelected) in visibleChildren)
        {
            var prefix = isSelected ? ">" : " ";
            var marker = GetGroupEditorMarker(child);
            var valueText = FormatGroupEditorValue(child);
            entries.Add(new ConfigDetailEntry($"{prefix} {marker} {child.DisplayName}: {valueText}", ConfigDetailEntryKind.Body));
        }

        entries.Add(new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body));
        entries.AddRange(TextDocumentFormatter.WrapParagraph(
                isEditingThisGroup
                    ? "Up and Down select fields. Space toggles boolean values. Enter or e edits the selected field. Press t to raw-edit text fields. Esc exits the group editor."
                    : "Press e to edit this section as a structured sub-editor.",
                width)
            .Select(text => new ConfigDetailEntry(text, ConfigDetailEntryKind.Meta)));

        return entries;
    }

    private IReadOnlyList<ConfigDetailEntry> BuildValueEditorEntries(ConfigBrowserNode node, int width)
    {
        if (_editMode == ConfigBrowserEditMode.Color && string.Equals(_editingPath, node.Path, StringComparison.OrdinalIgnoreCase))
        {
            return BuildColorEditorEntries(node, width);
        }

        if (_editMode == ConfigBrowserEditMode.Path && string.Equals(_editingPath, node.Path, StringComparison.OrdinalIgnoreCase))
        {
            if (_pathEditor.IsBrowsing)
            {
                return BuildPathPickerEntries(width);
            }

            return BuildPathEditorEntries(node, width);
        }

        if (_editMode == ConfigBrowserEditMode.Text && string.Equals(_editingPath, node.Path, StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                .. TextDocumentFormatter.WrapParagraph(_textInput.RenderWithCursor(), width)
                    .Select(text => new ConfigDetailEntry(text, ConfigDetailEntryKind.Body)),
                new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body),
                .. TextDocumentFormatter.WrapParagraph("Press Enter to stage this value, or Esc to cancel.", width)
                    .Select(text => new ConfigDetailEntry(text, ConfigDetailEntryKind.Meta)),
            ];
        }

        if (_editMode == ConfigBrowserEditMode.Enum && string.Equals(_editingPath, node.Path, StringComparison.OrdinalIgnoreCase))
        {
            _enumPicker.Refresh(Enum.GetNames(node.ValueType), Math.Max(1, _detailLines.Bounds.Height), _enumPicker.SelectedKey);
            var entries = new List<ConfigDetailEntry>();
            var enumNames = _enumPicker.Items;

            for (var index = 0; index < enumNames.Count; index++)
            {
                var enumName = enumNames[index];
                entries.Add(new ConfigDetailEntry($"{(index == _enumPicker.SelectedIndex ? ">" : " ")} {(index == _enumPicker.SelectedIndex ? "(*)" : "( )")} {enumName}", ConfigDetailEntryKind.Body));
            }

            entries.Add(new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body));
            entries.AddRange(TextDocumentFormatter.WrapParagraph(
                    "Up and Down move through enum options. Press Enter to stage the selected value, or Esc to cancel.",
                    width)
                .Select(text => new ConfigDetailEntry(text, ConfigDetailEntryKind.Meta)));
            return entries;
        }

        if (_editMode == ConfigBrowserEditMode.Collection && string.Equals(_editingPath, node.Path, StringComparison.OrdinalIgnoreCase))
        {
            return BuildCollectionEditorEntries(node, width);
        }

        if (IsPromptLayoutNode(node))
        {
            return BuildPromptLayoutEditorEntries(node, width);
        }

        if (node.EditorKind == ConfigBrowserEditorKind.Boolean)
        {
            return
            [
                new ConfigDetailEntry((GetEffectiveValue(node) is bool enabled && enabled) ? "[x] enabled" : "[ ] enabled", ConfigDetailEntryKind.Body),
                new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body),
                .. TextDocumentFormatter.WrapParagraph(
                        "Press Space to toggle this value, a to apply it, r to drop the staged change, or Shift+R to stage the default value.",
                        width)
                    .Select(text => new ConfigDetailEntry(text, ConfigDetailEntryKind.Meta)),
            ];
        }

        return
        [
            .. TextDocumentFormatter.WrapParagraph(
                    IsColorConfigNode(node)
                        ? "Press e to open the color picker, or t to raw-edit the color text."
                        : node.EditorKind == ConfigBrowserEditorKind.Collection && node.IsEditable
                            ? "Press e to open the collection editor. Use it to add, update, or remove collection items."
                        : node.EditorKind == ConfigBrowserEditorKind.Path
                            ? "Press e to open the path-aware editor, t to raw-edit the path text, or b while editing to browse the filesystem."
                            : "Press e to edit this value, a to apply staged changes, r to drop the staged value, or Shift+R to stage the default.",
                    width)
                .Select(text => new ConfigDetailEntry(text, ConfigDetailEntryKind.Meta)),
        ];
    }

    private IReadOnlyList<ConfigDetailEntry> BuildColorEditorEntries(ConfigBrowserNode node, int width)
    {
        _colorPicker.Refresh(BuildColorEditorOptions(node), Math.Max(1, _detailLines.Bounds.Height), _colorPicker.SelectedKey);

        var currentColor = _colorPicker.Items.Count == 0
            ? new ColorEditorOption(GetEditableText(GetEffectiveValue(node)), GetEditableText(GetEffectiveValue(node)))
            : _colorPicker.Items[Math.Clamp(_colorPicker.SelectedIndex, 0, _colorPicker.Items.Count - 1)];
        var entries = new List<ConfigDetailEntry>
        {
            new($"Selected: {FormatValuePreview(currentColor.Value)}", ConfigDetailEntryKind.Body),
        };

        entries.AddRange(BuildColorPreviewEntries(node, currentColor));
        entries.Add(new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body));

        for (var index = 0; index < _colorPicker.Items.Count; index++)
        {
            var option = _colorPicker.Items[index];
            var isSelected = index == _colorPicker.SelectedIndex;
            var isCurrent = ValuesEqual(GetEffectiveValue(node), option.Value);
            var prefix = isSelected ? ">" : " ";
            var marker = isCurrent ? "(*)" : "( )";
            entries.Add(new ConfigDetailEntry(BuildColorOptionLine(node, option, prefix, marker), ConfigDetailEntryKind.Preview));
        }

        entries.Add(new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body));
        entries.AddRange(TextDocumentFormatter.WrapParagraph(
                "Up and Down move through color options. Press Enter to stage the selected value, or Esc to cancel. Use t for raw hex or custom text.",
                width)
            .Select(text => new ConfigDetailEntry(text, ConfigDetailEntryKind.Meta)));

        return entries;
    }

    private IReadOnlyList<ConfigDetailEntry> BuildPathEditorEntries(ConfigBrowserNode node, int width)
    {
        var rawText = _pathEditor.RenderInputWithCursor();
        var plainText = _pathEditor.Text;
        var pathInfo = DescribePathValue(node, plainText);

        return
        [
            .. TextDocumentFormatter.WrapParagraph(rawText, width)
                .Select(text => new ConfigDetailEntry(text, ConfigDetailEntryKind.Body)),
            new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body),
            new ConfigDetailEntry($"Resolved Path: {pathInfo.ResolvedPath}", ConfigDetailEntryKind.Body),
            new ConfigDetailEntry($"Base Directory: {pathInfo.BaseDirectory}", ConfigDetailEntryKind.Body),
            new ConfigDetailEntry($"Exists: {pathInfo.ExistenceLabel}", ConfigDetailEntryKind.Body),
            new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body),
            .. TextDocumentFormatter.WrapParagraph(
                    "Press Enter to stage this path, b to browse the filesystem, or Esc to cancel. Use relative, absolute, or ~/ paths; the editor shows the resolved target while you type.",
                    width)
                .Select(text => new ConfigDetailEntry(text, ConfigDetailEntryKind.Meta)),
        ];
    }

    private IReadOnlyList<ConfigDetailEntry> BuildPathPickerEntries(int width)
    {
        var height = Math.Max(10, _detailLines.Bounds.Height);

        return
        [
            new ConfigDetailEntry("Filesystem Picker", ConfigDetailEntryKind.SectionHeading),
            new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body),
            .. _pathEditor.BuildPickerEntries(width, height).Select(text => new ConfigDetailEntry(text, ConfigDetailEntryKind.Body)),
        ];
    }

    private IReadOnlyList<ConfigDetailEntry> BuildPromptLayoutEditorEntries(ConfigBrowserNode node, int width)
    {
        if (GetEffectiveValue(node) is not string layoutText)
        {
            return
            [
                new ConfigDetailEntry("This prompt layout is not currently a string value.", ConfigDetailEntryKind.Body),
            ];
        }

        var unknownModules = ToshPromptRenderer.GetUnknownLayoutModules(layoutText);

        if (unknownModules.Count > 0)
        {
            return
            [
                new ConfigDetailEntry($"Current Layout: {layoutText}", ConfigDetailEntryKind.Body),
                new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body),
                .. TextDocumentFormatter.WrapParagraph(
                        $"Structured prompt layout editing is unavailable because this layout contains unknown modules: {string.Join(", ", unknownModules)}. Use raw text editing for this value instead.",
                        width)
                    .Select(text => new ConfigDetailEntry(text, ConfigDetailEntryKind.Meta)),
            ];
        }

        var isEditingThisLayout = _editMode == ConfigBrowserEditMode.PromptLayout && string.Equals(_editingPath, node.Path, StringComparison.OrdinalIgnoreCase);

        if (isEditingThisLayout)
        {
            _promptLayoutEditor.SetPageSize(Math.Max(1, _detailLines.Bounds.Height));
        }

        var items = isEditingThisLayout
            ? _promptLayoutEditor.Items
            : CreatePromptLayoutEditorItems(layoutText);
        var entries = new List<ConfigDetailEntry>(items.Count + 6)
        {
            new($"Current Layout: {(layoutText.Length == 0 ? "<none>" : layoutText)}", ConfigDetailEntryKind.Body),
            new(string.Empty, ConfigDetailEntryKind.Body),
        };

        var includedIndex = 0;

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var prefix = isEditingThisLayout && index == _promptLayoutEditor.SelectedIndex ? ">" : " ";
            var marker = item.Included ? "[x]" : "[ ]";
            var order = item.Included ? (++includedIndex).ToString(CultureInfo.InvariantCulture) : "-";
            var enabledPath = GetPromptModuleEnabledPath(item.Name);
            var enabledSuffix = enabledPath is not null &&
                                _schema.NodesByPath.TryGetValue(enabledPath, out var enabledNode) &&
                                GetEffectiveValue(enabledNode) is bool enabled &&
                                !enabled
                ? " [disabled]"
                : string.Empty;
            entries.Add(new ConfigDetailEntry($"{prefix} {marker} {order,2}. {item.Name}{enabledSuffix}", ConfigDetailEntryKind.Body));
        }

        entries.Add(new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body));
        entries.AddRange(TextDocumentFormatter.WrapParagraph(
                isEditingThisLayout
                    ? "Up and Down select modules. Space toggles inclusion. Shift+Up and Shift+Down reorder modules. Enter keeps the staged layout, and Esc restores the previous layout."
                    : "Press e to open the structured prompt layout editor, or t to edit the raw layout text.",
                width)
            .Select(text => new ConfigDetailEntry(text, ConfigDetailEntryKind.Meta)));

        return entries;
    }

    /// <summary>
    /// Renders a preview, or returns the one already rendered for these settings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Previews are expensive and were rebuilt on every frame. The prompt preview is the
    /// worst of them: it renders two complete sample prompts, which runs the prompt's own
    /// modules — including the one that reads git state. Selecting <c>Prompt</c> in the
    /// tree cost <b>17 ms per frame</b> against the help browser's 0.13 ms, so every
    /// arrow key paid it (<c>TUI-0014</c>).
    /// </para>
    /// <para>
    /// A preview depends on the staged values and the width it is drawn at, and on
    /// nothing else that matters — so it is kept until one of those changes. It also
    /// stops the preview drifting between frames from the clock and the working
    /// directory, which is what made these screens hard to snapshot.
    /// </para>
    /// </remarks>
    private IReadOnlyList<ConfigDetailEntry> CachedPreview(
        string key,
        int width,
        Func<int, IReadOnlyList<ConfigDetailEntry>> render)
    {
        if (_previewCache.TryGetValue(key, out var cached) &&
            cached.Version == _stagedVersion &&
            cached.Width == width)
        {
            return cached.Entries;
        }

        var entries = render(width);
        _previewCache[key] = (_stagedVersion, width, entries);

        return entries;
    }

    private IReadOnlyList<ConfigDetailEntry> BuildPromptPreviewEntries(int width)
        => CachedPreview("prompt", width, RenderPromptPreviewEntries);

    private IReadOnlyList<ConfigDetailEntry> BuildTableThemePreviewEntries(int width)
        => CachedPreview("theme.tables", width, RenderTableThemePreviewEntries);

    private IReadOnlyList<ConfigDetailEntry> BuildSyntaxThemePreviewEntries(int width)
        => CachedPreview("theme.syntax", width, RenderSyntaxThemePreviewEntries);

    private IReadOnlyList<ConfigDetailEntry> BuildTuiThemePreviewEntries(int width)
        => CachedPreview("theme.tui", width, RenderTuiThemePreviewEntries);

    private IReadOnlyList<ConfigDetailEntry> RenderPromptPreviewEntries(int width)
    {
        var previewRuntime = CreatePromptPreviewRuntime();
        var successPreview = ToshPromptRenderer.BuildPreviewLines(previewRuntime, 0, width);
        var failurePreview = ToshPromptRenderer.BuildPreviewLines(previewRuntime, 7, width);
        var headerLeft = string.Join(", ", ToshPromptRenderer.GetLayoutModules(previewRuntime.Config.Prompt.HeaderLeftLayout));
        var headerRight = string.Join(", ", ToshPromptRenderer.GetLayoutModules(previewRuntime.Config.Prompt.HeaderRightLayout));
        var promptLeft = string.Join(", ", ToshPromptRenderer.GetLayoutModules(previewRuntime.Config.Prompt.PromptLeftLayout));

        var entries = new List<ConfigDetailEntry>
        {
            new("Layout: two-line prompt", ConfigDetailEntryKind.Body),
            new($"Header Left: {(headerLeft.Length == 0 ? "<none>" : headerLeft)}", ConfigDetailEntryKind.Body),
            new($"Header Right: {(headerRight.Length == 0 ? "<none>" : headerRight)}", ConfigDetailEntryKind.Body),
            new($"Prompt Left: {(promptLeft.Length == 0 ? "<none>" : promptLeft)}", ConfigDetailEntryKind.Body),
            new(string.Empty, ConfigDetailEntryKind.Body),
            new("Sample Success Preview", ConfigDetailEntryKind.Body),
        };

        entries.AddRange(successPreview.Select(line => new ConfigDetailEntry(line, ConfigDetailEntryKind.Preview)));
        entries.Add(new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body));
        entries.Add(new ConfigDetailEntry("Sample Failure Preview", ConfigDetailEntryKind.Body));
        entries.AddRange(failurePreview.Select(line => new ConfigDetailEntry(line, ConfigDetailEntryKind.Preview)));

        return entries;
    }

    private IReadOnlyList<ConfigDetailEntry> BuildThemePreviewEntries(ConfigBrowserNode node, int width)
    {
        if (node.Path.StartsWith("Theme.Tables", StringComparison.OrdinalIgnoreCase))
        {
            return BuildTableThemePreviewEntries(width);
        }

        if (node.Path.StartsWith("Theme.Syntax", StringComparison.OrdinalIgnoreCase))
        {
            return BuildSyntaxThemePreviewEntries(width);
        }

        if (node.Path.StartsWith("Theme.Tui", StringComparison.OrdinalIgnoreCase))
        {
            return BuildTuiThemePreviewEntries(width);
        }

        return [];
    }

    private IReadOnlyList<ConfigDetailEntry> RenderTableThemePreviewEntries(int width)
    {
        var previewRuntime = CreateThemePreviewRuntime("Theme.Tables");
        var renderWidth = Math.Max(32, width);
        var preview = previewRuntime.Display.RenderMany(
            [
                new { Name = "alpha", Size = 12, State = "ok" },
                new { Name = "beta", Size = 24, State = "warn" },
                new { Name = "gamma", Size = 36, State = "busy" },
            ],
            new DisplayRenderOptions(previewRuntime.Display.Style, MaxWidth: renderWidth));

        return
        [
            new ConfigDetailEntry("Sample Table", ConfigDetailEntryKind.Body),
            .. preview.Split(Environment.NewLine).Select(text => new ConfigDetailEntry(text, ConfigDetailEntryKind.Preview)),
        ];
    }

    private IReadOnlyList<ConfigDetailEntry> RenderSyntaxThemePreviewEntries(int width)
    {
        var previewRuntime = CreateThemePreviewRuntime("Theme.Syntax");
        var sample = "var report = (df --total | summarize --sum _.Used); echo $report";
        var highlighted = SyntaxHighlighter.Highlight(sample, previewRuntime);

        return
        [
            new ConfigDetailEntry("Sample Command", ConfigDetailEntryKind.Body),
            new ConfigDetailEntry(TuiRenderHelpers.ClipPlain(highlighted, width), ConfigDetailEntryKind.Preview),
        ];
    }

    private IReadOnlyList<ConfigDetailEntry> RenderTuiThemePreviewEntries(int width)
    {
        var previewRuntime = CreateThemePreviewRuntime("Theme.Tui");
        var theme = previewRuntime.Config.Theme.Tui;
        var box = TuiBoxDrawing.GetBoxCharacters(theme.BoxStyle);
        var previewWidth = Math.Min(Math.Max(28, width), 56);
        var innerWidth = Math.Max(1, previewWidth - 2);

        return
        [
            new ConfigDetailEntry(TuiRenderHelpers.RenderTopBorder(previewWidth, "Preview Pane", theme, box), ConfigDetailEntryKind.Preview),
            new ConfigDetailEntry(TuiRenderHelpers.RenderBoxContentLine("Search: prompt", previewWidth, theme.SearchLabel, theme, box), ConfigDetailEntryKind.Preview),
            new ConfigDetailEntry(TuiRenderHelpers.RenderBoxContentLine("  Theme.Tui", previewWidth, theme.ListItem, theme, box), ConfigDetailEntryKind.Preview),
            new ConfigDetailEntry(TuiRenderHelpers.RenderBoxContentLine("> Prompt", previewWidth, theme.SelectedItem, theme, box), ConfigDetailEntryKind.Preview),
            new ConfigDetailEntry(TuiRenderHelpers.RenderBoxContentLine("Section Heading", previewWidth, theme.SectionHeading, theme, box), ConfigDetailEntryKind.Preview),
            new ConfigDetailEntry(TuiRenderHelpers.RenderBoxContentLine("meta: sample detail text", previewWidth, theme.Meta, theme, box), ConfigDetailEntryKind.Preview),
            new ConfigDetailEntry(TuiRenderHelpers.RenderBoxContentLine("example: e edit  a apply  q quit", previewWidth, theme.Example, theme, box), ConfigDetailEntryKind.Preview),
            new ConfigDetailEntry(TuiRenderHelpers.RenderBottomBorder(previewWidth, theme, box), ConfigDetailEntryKind.Preview),
            new ConfigDetailEntry(theme.Footer.Apply(TuiRenderHelpers.TrimOrPadPlain("focus:detail  dirty:2  e edit  s save", previewWidth)).ToAnsi(), ConfigDetailEntryKind.Preview),
            new ConfigDetailEntry($"Visible Width: {innerWidth}", ConfigDetailEntryKind.Meta),
        ];
    }

    /// <summary>
    /// A runtime configured as the user's staged settings would leave it, for rendering a
    /// live preview of a theme or prompt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The runtime is built once per preview and kept; only the values are re-applied.
    /// Building it was previously part of drawing, and <see cref="ToshRuntime.CreateDefault"/>
    /// is not a cheap object — it loads the standard library, registers every built-in
    /// type and command, and starts a background walk of the platform type index. Doing
    /// that per frame made the config browser cost 676 us and 1.18 MB against the help
    /// browser's 59 us and 220 KB at the same size, which is to say per arrow key, and
    /// once a second on any screen with a refresh interval (<c>TUI-0014</c>).
    /// </para>
    /// <para>
    /// One runtime per set of prefixes rather than one shared between them, so each
    /// preview still sees only the values it asked for and nothing carried over from a
    /// preview the user looked at earlier.
    /// </para>
    /// </remarks>
    private ToshRuntime PreviewRuntime(string key, Func<ConfigBrowserNode, bool> selects)
    {
        if (!_previewRuntimes.TryGetValue(key, out var previewRuntime))
        {
            previewRuntime = ToshRuntime.CreateDefault(TextWriter.Null, TextWriter.Null);
            _previewRuntimes[key] = previewRuntime;
        }

        previewRuntime.CurrentDirectory = _runtime.CurrentDirectory;

        // The schema does not change, so the nodes a preview covers are worked out once.
        if (!_previewLeaves.TryGetValue(key, out var leaves))
        {
            leaves = EnumerateLeafNodes(_schema.Root).Where(selects).ToArray();
            _previewLeaves[key] = leaves;
        }

        // Values are re-applied every time: they are what the user is editing.
        foreach (var leaf in leaves)
        {
            previewRuntime.ObjectAccessor.SetValue(previewRuntime.Config, leaf.Path, GetEffectiveValue(leaf));
        }

        return previewRuntime;
    }

    private ToshRuntime CreateThemePreviewRuntime(params string[] pathPrefixes)
        => PreviewRuntime(
            string.Join('|', pathPrefixes),
            node => node.IsEditable &&
                    pathPrefixes.Any(prefix => node.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));

    private ToshRuntime CreatePromptPreviewRuntime()
        => PreviewRuntime(
            "<prompt>",
            node => node.IsEditable &&
                    (node.Path.StartsWith("Prompt.", StringComparison.OrdinalIgnoreCase) ||
                     node.Path.StartsWith("Theme.Prompt.", StringComparison.OrdinalIgnoreCase)));

    private static bool ShouldShowPromptPreview(ConfigBrowserNode node)
    {
        return node.Path.Equals("Prompt", StringComparison.OrdinalIgnoreCase) ||
               node.Path.StartsWith("Prompt.", StringComparison.OrdinalIgnoreCase) ||
               node.Path.Equals("Theme.Prompt", StringComparison.OrdinalIgnoreCase) ||
               node.Path.StartsWith("Theme.Prompt.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldShowThemePreview(ConfigBrowserNode node)
    {
        return node.Path.StartsWith("Theme.Tables", StringComparison.OrdinalIgnoreCase) ||
               node.Path.StartsWith("Theme.Syntax", StringComparison.OrdinalIgnoreCase) ||
               node.Path.StartsWith("Theme.Tui", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldShowStartupActions(ConfigBrowserNode node)
    {
        return node.Path.Equals("Startup", StringComparison.OrdinalIgnoreCase) ||
               node.Path.StartsWith("Startup.", StringComparison.OrdinalIgnoreCase);
    }

    private T? GetChildValue<T>(ConfigBrowserNode parent, string childName)
    {
        var child = parent.Children.FirstOrDefault(item => string.Equals(item.Name, childName, StringComparison.OrdinalIgnoreCase));

        if (child is null)
        {
            return default;
        }

        var value = GetEffectiveValue(child);

        return value is T typed ? typed : default;
    }

    private IReadOnlyList<ColorEditorOption> BuildColorEditorOptions(ConfigBrowserNode node)
    {
        var options = new List<ColorEditorOption>
        {
            new("<none>", null),
        };
        var effectiveText = GetEffectiveValue(node) as string;

        if (!string.IsNullOrWhiteSpace(effectiveText) &&
            !StyledText.SupportedNamedColors.Contains(effectiveText, StringComparer.OrdinalIgnoreCase))
        {
            options.Add(new ColorEditorOption(effectiveText, effectiveText));
        }

        options.AddRange(StyledText.SupportedNamedColors.Select(color => new ColorEditorOption(color, color)));
        return options;
    }

    private int GetCurrentColorSelectionIndex(ConfigBrowserNode node)
    {
        var effectiveText = GetEffectiveValue(node) as string;

        var items = BuildColorEditorOptions(node);

        for (var index = 0; index < items.Count; index++)
        {
            if (string.Equals(items[index].Value, effectiveText, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return 0;
    }

    private IReadOnlyList<ConfigDetailEntry> BuildColorPreviewEntries(ConfigBrowserNode node, ColorEditorOption option)
    {
        var isBackground = node.Name.Contains("Background", StringComparison.OrdinalIgnoreCase);
        var sample = option.Value is null
            ? " Preview: Sample Text 123 "
            : isBackground
                ? new StyledText(" Preview: Sample Text 123 ", Foreground: "bright-white", Background: option.Value).ToAnsi()
                : new StyledText(" Preview: Sample Text 123 ", Foreground: option.Value).ToAnsi();

        return
        [
            new ConfigDetailEntry(sample, ConfigDetailEntryKind.Preview),
        ];
    }

    private string BuildColorOptionLine(ConfigBrowserNode node, ColorEditorOption option, string prefix, string marker)
    {
        var isBackground = node.Name.Contains("Background", StringComparison.OrdinalIgnoreCase);
        var sample = option.Value is null
            ? " sample "
            : isBackground
                ? new StyledText(" sample ", Foreground: "bright-white", Background: option.Value).ToAnsi()
                : new StyledText(" sample ", Foreground: option.Value).ToAnsi();

        return $"{prefix} {marker} {option.Label,-18} {sample}";
    }

    private PathValueDescription DescribePathValue(ConfigBrowserNode node, string rawText)
    {
        var baseDirectory = GetPathResolutionBaseDirectory(node);
        string resolvedPath;

        try
        {
            resolvedPath = PathUtilities.ResolvePath(baseDirectory, rawText);
        }
        catch
        {
            resolvedPath = rawText;
        }

        var existenceLabel = Directory.Exists(resolvedPath)
            ? "directory"
            : File.Exists(resolvedPath)
                ? "file"
                : "missing";

        return new PathValueDescription(baseDirectory, resolvedPath, existenceLabel);
    }

    private string GetPathResolutionBaseDirectory(ConfigBrowserNode node)
    {
        if (node.Path.Equals("Startup.RootDirectory", StringComparison.OrdinalIgnoreCase))
        {
            return _runtime.CurrentDirectory;
        }

        if (node.Path.StartsWith("Startup.", StringComparison.OrdinalIgnoreCase) &&
            _schema.NodesByPath.TryGetValue("Startup.RootDirectory", out var rootNode))
        {
            var rootText = GetEffectiveValue(rootNode) as string;
            return PathUtilities.ResolvePath(_runtime.CurrentDirectory, rootText ?? _runtime.Config.Startup.RootDirectory);
        }

        if (node.Path.Equals("History.FilePath", StringComparison.OrdinalIgnoreCase))
        {
            var defaultPath = GetDefaultValue(node) as string;
            var defaultRoot = string.IsNullOrWhiteSpace(defaultPath) ? null : Path.GetDirectoryName(defaultPath);

            if (!string.IsNullOrWhiteSpace(defaultRoot))
            {
                return defaultRoot;
            }
        }

        return _runtime.CurrentDirectory;
    }

    private IReadOnlyList<ConfigDetailEntry> BuildValidationEntries(IReadOnlyList<TuiValidationMessage> messages, int width)
    {
        return TuiValidationFormatter.BuildEntries(messages, width)
            .Select(text => new ConfigDetailEntry(text, ConfigDetailEntryKind.Meta))
            .ToArray();
    }

    private IReadOnlyList<ConfigDetailEntry> BuildStagedDiffEntries(ConfigBrowserNode node, int width)
    {
        var stagedLeaves = EnumerateLeafNodes(node)
            .Where(leaf => leaf.Path.Length > 0 && _stagedValues.ContainsKey(leaf.Path))
            .OrderBy(leaf => leaf.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (stagedLeaves.Length == 0)
        {
            return
            [
                new ConfigDetailEntry("No staged changes are present in this subtree.", ConfigDetailEntryKind.Meta),
            ];
        }

        var rows = new List<TuiFormRow>(stagedLeaves.Length * 4);

        foreach (var leaf in stagedLeaves)
        {
            var relativePath = node.Path.Length == 0
                ? leaf.Path
                : leaf.Path[node.Path.Length..].TrimStart('.');
            var liveText = FormatValuePreview(GetCurrentValue(leaf));
            var stagedText = FormatValuePreview(_stagedValues[leaf.Path]);
            var defaultText = FormatValuePreview(GetDefaultValue(leaf));

            rows.Add(new TuiFormRow(relativePath, Kind: TuiFormRowKind.Body));
            rows.Add(new TuiFormRow("live", liveText, TuiFormRowKind.Meta));
            rows.Add(new TuiFormRow("staged", stagedText, TuiFormRowKind.Meta));

            if (!string.Equals(defaultText, stagedText, StringComparison.Ordinal) ||
                !string.Equals(defaultText, liveText, StringComparison.Ordinal))
            {
                rows.Add(new TuiFormRow("default", defaultText, TuiFormRowKind.Meta));
            }
        }

        return BuildFormEntries(rows, width, labelWidth: 14);
    }

    private IReadOnlyList<ConfigDetailEntry> BuildCollectionEntries(ConfigBrowserNode node, int width)
    {
        var value = GetEffectiveValue(node);

        if (value is not System.Collections.IEnumerable enumerable || value is string)
        {
            return
            [
                new ConfigDetailEntry("This value is not currently a renderable collection.", ConfigDetailEntryKind.Meta),
            ];
        }

        var items = enumerable.Cast<object?>().ToArray();

        if (items.Length == 0)
        {
            return
            [
                new ConfigDetailEntry("Item Count: 0", ConfigDetailEntryKind.Body),
                new ConfigDetailEntry("This collection is empty.", ConfigDetailEntryKind.Meta),
            ];
        }

        var rendered = _runtime.Display.RenderMany(
            items,
            new DisplayRenderOptions(_runtime.Display.Style, MaxWidth: Math.Max(32, width)));

        return
        [
            new ConfigDetailEntry($"Item Count: {items.Length}", ConfigDetailEntryKind.Body),
            new ConfigDetailEntry(
                node.IsEditable
                    ? "Press e to open the collection editor for this value."
                    : "This collection is currently view-only in config browse.",
                ConfigDetailEntryKind.Meta),
            new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body),
            .. rendered.Split(Environment.NewLine).Select(text => new ConfigDetailEntry(text, ConfigDetailEntryKind.Preview)),
        ];
    }

    private IReadOnlyList<ConfigDetailEntry> BuildCollectionEditorEntries(ConfigBrowserNode node, int width)
    {
        RefreshCollectionEditor(node, _collectionEditor.EditingItemKey);
        var entries = new List<ConfigDetailEntry>();

        if (_collectionEditor.InputMode != TuiCollectionEditorInputMode.None)
        {
            var heading = _collectionEditor.InputMode == TuiCollectionEditorInputMode.AddItem
                ? "New override (Type = Column1, Column2)"
                : $"Columns for {_collectionEditor.EditingItemKey}";
            entries.Add(new ConfigDetailEntry(heading, ConfigDetailEntryKind.Body));
            entries.AddRange(TextDocumentFormatter.WrapParagraph(_collectionEditor.RenderInputWithCursor(), width)
                .Select(text => new ConfigDetailEntry(text, ConfigDetailEntryKind.Body)));
            entries.Add(new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body));
            entries.AddRange(TextDocumentFormatter.WrapParagraph(
                    _collectionEditor.InputMode == TuiCollectionEditorInputMode.AddItem
                        ? "Enter a type name followed by one or more columns, for example: System.String = Length, Chars. Press Enter to stage it or Esc to cancel."
                        : "Enter a comma-separated list of columns for the selected item. Press Enter to stage it or Esc to cancel.",
                    width)
                .Select(text => new ConfigDetailEntry(text, ConfigDetailEntryKind.Meta)));
            entries.Add(new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body));
        }

        if (_collectionEditor.Items.Count == 0)
        {
            entries.Add(new ConfigDetailEntry("No collection items are currently defined.", ConfigDetailEntryKind.Meta));
        }
        else
        {
            var rows = _collectionEditor.GetVisibleItems()
                .Select(visible => new TuiFormRow(
                    visible.Item.Label,
                    visible.Item.Summary,
                    IsSelected: visible.IsSelected))
                .ToArray();

            entries.AddRange(BuildFormEntries(rows, width, labelWidth: Math.Clamp(width / 3, 18, 36)));
        }

        entries.Add(new ConfigDetailEntry(string.Empty, ConfigDetailEntryKind.Body));
        entries.AddRange(TextDocumentFormatter.WrapParagraph(
                _collectionEditor.InputMode == TuiCollectionEditorInputMode.None
                    ? "Up and Down select collection items. Enter or e edits the selected item's columns. Press n to add an override, Delete or r to remove one, a to apply, s to save, and Esc to close the collection editor."
                    : "Press Enter to keep the staged collection edit, or Esc to cancel.",
                width)
            .Select(text => new ConfigDetailEntry(text, ConfigDetailEntryKind.Meta)));

        return entries;
    }

    private IReadOnlyList<ConfigDetailEntry> BuildStartupActionEntries(int width)
    {
        var startup = _runtime.Config.Startup;
        var configPath = startup.ResolvePath(startup.ConfigFilePath);
        var profilePath = startup.ResolvePath(startup.ProfilePath);
        var autoloadDirectory = startup.ResolvePath(startup.AutoloadDirectory);
        var startupDirty = _schema.NodesByPath.TryGetValue("Startup", out var startupNode) && CountStagedChanges(startupNode) > 0;
        var usesLiveSettingsMessage = startupDirty
            ? "Reload and init use the live startup settings. Apply staged Startup edits first if you want those paths to take effect now."
            : "Reload re-runs config.tosh, profile.tosh, and autoload modules for this session.";

        var rows = new List<TuiFormRow>
        {
            new("Root Directory", startup.RootDirectory),
            new("Config File", configPath),
            new("Profile File", profilePath),
            new("Autoload Directory", autoloadDirectory),
            new(string.Empty, Kind: TuiFormRowKind.Body),
            new("Info", usesLiveSettingsMessage, TuiFormRowKind.Meta),
            new("Keys", "Press l to reload startup configuration into the current session. Press i to create any missing startup files and the autoload directory in the current root directory.", TuiFormRowKind.Meta),
        };

        return BuildFormEntries(rows, width, labelWidth: 18);
    }

    private IReadOnlyList<ConfigDetailEntry> BuildFormEntries(IReadOnlyList<TuiFormRow> rows, int width, int labelWidth = 18)
    {
        return TuiFormLayout.BuildEntries(rows, width, labelWidth)
            .Select(entry => new ConfigDetailEntry(
                entry.Text,
                entry.Kind switch
                {
                    TuiFormRowKind.Meta => ConfigDetailEntryKind.Meta,
                    TuiFormRowKind.Preview => ConfigDetailEntryKind.Preview,
                    _ => ConfigDetailEntryKind.Body,
                }))
            .ToArray();
    }
}
