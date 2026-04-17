using System.Globalization;
using System.Text;
using Tosh.Core;
using Tosh.Cli;
using Tosh.Tui.Requests;

namespace Tosh.Cli.Tui;

internal sealed class ConfigBrowserScreen : ITuiScreen
{
    private const int SearchBoxHeight = 3;
    private const string ManagedConfigBlockStart = "# >>> tosh config browse >>>";
    private const string ManagedConfigBlockEnd = "# <<< tosh config browse <<<";

    private readonly ToshRuntime _runtime;
    private readonly ConfigBrowserSchema _schema;
    private readonly TuiListState<ConfigBrowserListEntry> _tree = new();
    private readonly TuiScrollState _detailScroll = new();
    private readonly TuiConfirmationDialogState _confirmDialog = new();
    private readonly TuiPathEditorState _pathEditor = new();
    private readonly TuiGroupEditorState<ConfigBrowserNode> _groupEditor = new();
    private readonly TuiOptionPickerState<string> _enumPicker = new();
    private readonly TuiOptionPickerState<ColorEditorOption> _colorPicker = new();
    private readonly TuiOrderedToggleEditorState<PromptLayoutEditorItem> _promptLayoutEditor = new();
    private readonly HashSet<string> _expandedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, object?> _stagedValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly TuiTextInputState _textInput = new();
    private readonly TuiCollectionEditorState<ConfigCollectionEditorItem> _collectionEditor = new();
    private readonly List<ConfigEditSnapshotEntry> _liveEditSnapshot = [];

    private ConfigBrowserFocus _focus = ConfigBrowserFocus.Tree;
    private ConfigBrowserConfirmAction _pendingConfirmAction;
    private ConfigBrowserEditMode _editMode;
    private string _query;
    private string? _selectedPath;
    private string? _editingPath;
    private string? _groupEditingPath;
    private string? _statusMessage;
    private TuiRect _lastSearchRect;
    private TuiRect _lastTreeRect;
    private TuiRect _lastDetailRect;

    public ConfigBrowserScreen(ToshRuntime runtime, ConfigBrowseRequest request)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(request);

        _runtime = runtime;
        _schema = ConfigBrowserSchemaBuilder.Build(runtime);
        _query = request.InitialQuery ?? string.Empty;

        foreach (var child in _schema.Root.Children)
        {
            _expandedPaths.Add(child.Path);
        }

        if (!string.IsNullOrWhiteSpace(request.InitialPath))
        {
            var normalized = ConfigPathUtilities.NormalizeMemberPath(runtime.Config, request.InitialPath);
            ExpandAncestors(normalized);
            _selectedPath = normalized;
        }

        SyncTree(pageSize: 12);
    }

    public TuiFrame Render(TuiSize size)
    {
        var root = new TuiRect(0, 0, Math.Max(20, size.Width), Math.Max(8, size.Height));
        var (searchRect, restRows) = TuiSplitLayout.SplitRows(root, SearchBoxHeight, gap: 0);
        var (contentRows, footerRow) = TuiSplitLayout.SplitRows(restRows, Math.Max(4, restRows.Height - 1), gap: 0);
        var sidebarWidth = Math.Clamp(contentRows.Width / 3, 28, Math.Max(28, contentRows.Width - 24));
        var (treeRect, detailRect) = TuiSplitLayout.SplitColumns(contentRows, sidebarWidth, gap: 1);

        _lastSearchRect = searchRect;
        _lastTreeRect = treeRect;
        _lastDetailRect = detailRect;

        SyncTree(Math.Max(1, treeRect.Height - 2));
        var detailLines = BuildDetailEntries(Math.Max(1, detailRect.Width - 2));
        _detailScroll.SetDimensions(detailLines.Count, Math.Max(1, detailRect.Height - 2));

        var builder = new StringBuilder();
        builder.Append(RenderSearchBox(searchRect.Width));
        builder.AppendLine();
        builder.Append(RenderContentRows(treeRect, detailRect, detailLines));
        builder.Append(RenderFooter(footerRow.Width));
        return new TuiFrame(builder.ToString());
    }

    public TuiScreenResult HandleInput(TuiInputEvent input)
    {
        if (input.IsKey)
            return HandleKey(input.Key);

        var mouse = input.Mouse;

        // Scroll wheel in detail pane
        if (mouse.Action == TuiMouseAction.Scroll && mouse.HitsRect(_lastDetailRect))
        {
            if (mouse.Button == TuiMouseButton.ScrollUp)
                _detailScroll.LineUp();
            else if (mouse.Button == TuiMouseButton.ScrollDown)
                _detailScroll.LineDown();

            return TuiScreenResult.Continue;
        }

        // Scroll wheel in tree pane
        if (mouse.Action == TuiMouseAction.Scroll && mouse.HitsRect(_lastTreeRect))
        {
            if (mouse.Button == TuiMouseButton.ScrollUp)
                _tree.MovePrevious();
            else if (mouse.Button == TuiMouseButton.ScrollDown)
                _tree.MoveNext();

            if (_tree.TryGetSelected(out var scrollSelected))
            {
                _selectedPath = scrollSelected.Node.Path;
                _detailScroll.Home();
            }

            return TuiScreenResult.Continue;
        }

        // Click to switch focus between panes
        if (mouse.Action == TuiMouseAction.Press && mouse.Button == TuiMouseButton.Left)
        {
            if (mouse.HitsRect(_lastSearchRect))
            {
                _focus = ConfigBrowserFocus.Search;
                return TuiScreenResult.Continue;
            }

            if (mouse.HitsRect(_lastTreeRect))
            {
                _focus = ConfigBrowserFocus.Tree;

                // Click on a specific tree item
                var treeRow = mouse.Row - _lastTreeRect.Top - 1; // -1 for border
                if (treeRow >= 0)
                {
                    var range = _tree.Scroll.GetVisibleRange();

                    if (treeRow < range.Length)
                    {
                        _tree.SelectIndex(range.Start + treeRow);

                        if (_tree.TryGetSelected(out var clickSelected))
                        {
                            _selectedPath = clickSelected.Node.Path;
                            _detailScroll.Home();
                        }
                    }
                }

                return TuiScreenResult.Continue;
            }

            if (mouse.HitsRect(_lastDetailRect))
            {
                _focus = ConfigBrowserFocus.Detail;
                return TuiScreenResult.Continue;
            }
        }

        return TuiScreenResult.Continue;
    }

    public TuiScreenResult HandleKey(ConsoleKeyInfo key)
    {
        if (_confirmDialog.IsOpen)
        {
            var confirmationResult = _confirmDialog.HandleKey(key);
            var confirmAction = _pendingConfirmAction;

            switch (confirmationResult.Kind)
            {
                case TuiConfirmationDialogResultKind.Confirmed:
                    _confirmDialog.Close();
                    _pendingConfirmAction = ConfigBrowserConfirmAction.None;
                    return confirmAction switch
                    {
                        ConfigBrowserConfirmAction.Exit => TuiScreenResult.Exit,
                        ConfigBrowserConfirmAction.ReloadStartup => ExecuteReloadStartupAction(),
                        ConfigBrowserConfirmAction.InitializeStartup => ExecuteInitializeStartupAction(),
                        _ => TuiScreenResult.Continue,
                    };
                case TuiConfirmationDialogResultKind.Cancelled:
                    _confirmDialog.Close();
                    _pendingConfirmAction = ConfigBrowserConfirmAction.None;
                    _statusMessage = confirmAction switch
                    {
                        ConfigBrowserConfirmAction.ReloadStartup => "Reload cancelled.",
                        ConfigBrowserConfirmAction.InitializeStartup => "Startup initialization cancelled.",
                        _ => "Exit cancelled.",
                    };
                    return TuiScreenResult.Continue;
            }

            return TuiScreenResult.Continue;
        }

        if (_editMode != ConfigBrowserEditMode.None)
        {
            return HandleEditorKey(key);
        }

        if (key.Key == ConsoleKey.Q && key.Modifiers == 0)
        {
            if (_stagedValues.Count > 0)
            {
                _pendingConfirmAction = ConfigBrowserConfirmAction.Exit;
                _confirmDialog.Open(
                    "Discard Staged Changes?",
                    $"You have {_stagedValues.Count} staged change{(_stagedValues.Count == 1 ? string.Empty : "s")}. Discard them and quit?",
                    confirmLabel: "Discard & Quit",
                    cancelLabel: "Stay");
                _focus = ConfigBrowserFocus.Detail;
                _statusMessage = null;
                return TuiScreenResult.Continue;
            }

            return TuiScreenResult.Exit;
        }

        if (_focus != ConfigBrowserFocus.Search && !key.Modifiers.HasFlag(ConsoleModifiers.Control) && !key.Modifiers.HasFlag(ConsoleModifiers.Alt))
        {
            switch (key.Key)
            {
                case ConsoleKey.Spacebar when TryToggleSelectedBoolean():
                    return TuiScreenResult.Continue;
                case ConsoleKey.E when BeginEditSelectedNode():
                    return TuiScreenResult.Continue;
                case ConsoleKey.T when BeginRawEditSelectedNode():
                    return TuiScreenResult.Continue;
                case ConsoleKey.S when SaveConfiguration():
                    return TuiScreenResult.Continue;
                case ConsoleKey.A when !key.Modifiers.HasFlag(ConsoleModifiers.Shift) && ApplyStagedChanges():
                    return TuiScreenResult.Continue;
                case ConsoleKey.R when key.Modifiers.HasFlag(ConsoleModifiers.Shift) && ResetSelectedNodeToDefaults():
                    return TuiScreenResult.Continue;
                case ConsoleKey.R when !key.Modifiers.HasFlag(ConsoleModifiers.Shift) && RevertSelectedNode():
                    return TuiScreenResult.Continue;
                case ConsoleKey.L when TryReloadStartupAction():
                    return TuiScreenResult.Continue;
                case ConsoleKey.I when TryInitializeStartupAction():
                    return TuiScreenResult.Continue;
            }
        }

        switch (key.Key)
        {
            case ConsoleKey.Tab:
                CycleFocus(reverse: key.Modifiers.HasFlag(ConsoleModifiers.Shift));
                return TuiScreenResult.Continue;
            case ConsoleKey.Oem2 when key.KeyChar == '/':
                _focus = ConfigBrowserFocus.Search;
                return TuiScreenResult.Continue;
            case ConsoleKey.LeftArrow:
                if (_focus == ConfigBrowserFocus.Tree && CollapseSelectedGroup())
                {
                    return TuiScreenResult.Continue;
                }

                _focus = ConfigBrowserFocus.Tree;
                return TuiScreenResult.Continue;
            case ConsoleKey.RightArrow:
                if (_focus == ConfigBrowserFocus.Tree)
                {
                    if (!ExpandSelectedGroup())
                    {
                        _focus = ConfigBrowserFocus.Detail;
                    }

                    return TuiScreenResult.Continue;
                }

                _focus = ConfigBrowserFocus.Detail;
                return TuiScreenResult.Continue;
            case ConsoleKey.Enter:
                return HandleEnterKey();
        }

        return _focus switch
        {
            ConfigBrowserFocus.Search => HandleSearchKey(key),
            ConfigBrowserFocus.Tree => HandleTreeKey(key),
            _ => HandleDetailKey(key),
        };
    }

    internal IReadOnlyList<string> BuildSidebarLabels()
    {
        return BuildVisibleEntries()
            .Select(entry => entry.Label)
            .ToArray();
    }

    internal IReadOnlyList<string> BuildDetailLines(int width)
    {
        return BuildDetailEntries(width)
            .Select(entry => entry.Text)
            .ToArray();
    }

    internal bool SelectSidebarEntryContaining(string text)
    {
        var entry = BuildVisibleEntries()
            .FirstOrDefault(item =>
                item.Label.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                item.Node.Path.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                item.Node.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            return false;
        }

        _selectedPath = entry.Node.Path;
        SyncTree(_tree.Scroll.PageSize > 0 ? _tree.Scroll.PageSize : 10);
        return true;
    }

    internal string? CurrentPath => _selectedPath;

    private TuiScreenResult HandleEnterKey()
    {
        if (_focus == ConfigBrowserFocus.Search)
        {
            _focus = ConfigBrowserFocus.Tree;
            return TuiScreenResult.Continue;
        }

        if (!_tree.TryGetSelected(out var entry))
        {
            return TuiScreenResult.Continue;
        }

        if (entry.Node.Kind == ConfigBrowserNodeKind.Group)
        {
            ToggleExpanded(entry.Node.Path);
            return TuiScreenResult.Continue;
        }

        _focus = ConfigBrowserFocus.Detail;
        return TuiScreenResult.Continue;
    }

    private TuiScreenResult HandleSearchKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Escape:
                _focus = ConfigBrowserFocus.Tree;
                return TuiScreenResult.Continue;
            case ConsoleKey.Backspace:
                if (_query.Length > 0)
                {
                    _query = _query[..^1];
                    _detailScroll.Home();
                    SyncTree(_tree.Scroll.PageSize > 0 ? _tree.Scroll.PageSize : 10);
                }

                return TuiScreenResult.Continue;
        }

        if (!char.IsControl(key.KeyChar))
        {
            _query += key.KeyChar;
            _detailScroll.Home();
            SyncTree(_tree.Scroll.PageSize > 0 ? _tree.Scroll.PageSize : 10);
        }

        return TuiScreenResult.Continue;
    }

    private TuiScreenResult HandleTreeKey(ConsoleKeyInfo key)
    {
        var handled = key.Key switch
        {
            ConsoleKey.UpArrow => _tree.MovePrevious(),
            ConsoleKey.DownArrow => _tree.MoveNext(),
            ConsoleKey.PageUp => _tree.PageUp(),
            ConsoleKey.PageDown => _tree.PageDown(),
            ConsoleKey.Home => _tree.Home(),
            ConsoleKey.End => _tree.End(),
            _ => false,
        };

        if (handled && _tree.TryGetSelected(out var selected))
        {
            _selectedPath = selected.Node.Path;
            _detailScroll.Home();
        }

        return TuiScreenResult.Continue;
    }

    private TuiScreenResult HandleDetailKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                _detailScroll.LineUp();
                break;
            case ConsoleKey.DownArrow:
                _detailScroll.LineDown();
                break;
            case ConsoleKey.PageUp:
                _detailScroll.PageUp();
                break;
            case ConsoleKey.PageDown:
                _detailScroll.PageDown();
                break;
            case ConsoleKey.Home:
                _detailScroll.Home();
                break;
            case ConsoleKey.End:
                _detailScroll.End();
                break;
        }

        return TuiScreenResult.Continue;
    }

    private void SyncTree(int pageSize)
    {
        var entries = BuildVisibleEntries();
        var selectedPath = _selectedPath;

        _tree.SetItems(entries, pageSize);

        if (selectedPath is not null)
        {
            var selectedIndex = entries
                .Select((entry, index) => new { entry, index })
                .FirstOrDefault(item => string.Equals(item.entry.Node.Path, selectedPath, StringComparison.OrdinalIgnoreCase))
                ?.index ?? -1;

            if (selectedIndex >= 0)
            {
                _tree.SelectIndex(selectedIndex);
                _selectedPath = entries[selectedIndex].Node.Path;
                return;
            }
        }

        if (_tree.TryGetSelected(out var selected))
        {
            _selectedPath = selected.Node.Path;
        }
    }

    private IReadOnlyList<ConfigBrowserListEntry> BuildVisibleEntries()
    {
        var entries = new List<ConfigBrowserListEntry>();

        foreach (var child in _schema.Root.Children)
        {
            AppendVisibleEntries(child, depth: 0, entries);
        }

        return entries;
    }

    private bool AppendVisibleEntries(ConfigBrowserNode node, int depth, List<ConfigBrowserListEntry> entries)
    {
        var queryActive = !string.IsNullOrWhiteSpace(_query);
        var selfMatches = queryActive && NodeMatchesQuery(node, _query);
        var childEntries = new List<ConfigBrowserListEntry>();
        var descendantMatches = false;

        if (node.Children.Count > 0)
        {
            foreach (var child in node.Children)
            {
                descendantMatches |= AppendVisibleEntries(child, depth + 1, childEntries);
            }
        }

        if (queryActive && !selfMatches && !descendantMatches)
        {
            return false;
        }

        if (queryActive && selfMatches && node.Children.Count > 0)
        {
            childEntries.Clear();

            foreach (var child in node.Children)
            {
                AppendAllEntries(child, depth + 1, childEntries);
            }

            descendantMatches = childEntries.Count > 0;
        }

        var isExpanded = node.Kind == ConfigBrowserNodeKind.Group &&
                         (queryActive
                             ? selfMatches || descendantMatches
                             : _expandedPaths.Contains(node.Path));
        entries.Add(new ConfigBrowserListEntry(node, depth, isExpanded, HasStagedChanges(node)));

        if (isExpanded)
        {
            entries.AddRange(childEntries);
        }

        return true;
    }

    private void AppendAllEntries(ConfigBrowserNode node, int depth, List<ConfigBrowserListEntry> entries)
    {
        var isExpanded = node.Kind == ConfigBrowserNodeKind.Group;
        entries.Add(new ConfigBrowserListEntry(node, depth, isExpanded, HasStagedChanges(node)));

        if (!isExpanded)
        {
            return;
        }

        foreach (var child in node.Children)
        {
            AppendAllEntries(child, depth + 1, entries);
        }
    }

    private static bool NodeMatchesQuery(ConfigBrowserNode node, string query)
    {
        return node.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               node.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               node.Path.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               node.TypeName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               node.EditorKind.ToString().Contains(query, StringComparison.OrdinalIgnoreCase);
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
                _enumPicker.Refresh(enumNames, Math.Max(1, _detailScroll.PageSize), _enumPicker.SelectedKey);
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
            _enumPicker.Refresh(Enum.GetNames(node.ValueType), Math.Max(1, _detailScroll.PageSize), _enumPicker.SelectedKey);
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
        _colorPicker.Refresh(BuildColorEditorOptions(node), Math.Max(1, _detailScroll.PageSize), _colorPicker.SelectedKey);

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
        var height = Math.Max(10, _detailScroll.PageSize);

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
            _promptLayoutEditor.SetPageSize(Math.Max(1, _detailScroll.PageSize));
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

    private IReadOnlyList<ConfigDetailEntry> BuildPromptPreviewEntries(int width)
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

    private IReadOnlyList<ConfigDetailEntry> BuildTableThemePreviewEntries(int width)
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

    private IReadOnlyList<ConfigDetailEntry> BuildSyntaxThemePreviewEntries(int width)
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

    private IReadOnlyList<ConfigDetailEntry> BuildTuiThemePreviewEntries(int width)
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

    private ToshRuntime CreateThemePreviewRuntime(params string[] pathPrefixes)
    {
        var previewRuntime = ToshRuntime.CreateDefault(TextWriter.Null, TextWriter.Null);
        previewRuntime.CurrentDirectory = _runtime.CurrentDirectory;

        foreach (var leaf in EnumerateLeafNodes(_schema.Root).Where(node =>
                     node.IsEditable &&
                     pathPrefixes.Any(prefix => node.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))))
        {
            previewRuntime.ObjectAccessor.SetValue(previewRuntime.Config, leaf.Path, GetEffectiveValue(leaf));
        }

        return previewRuntime;
    }

    private ToshRuntime CreatePromptPreviewRuntime()
    {
        var previewRuntime = ToshRuntime.CreateDefault(TextWriter.Null, TextWriter.Null);
        previewRuntime.CurrentDirectory = _runtime.CurrentDirectory;

        foreach (var leaf in EnumerateLeafNodes(_schema.Root).Where(node =>
                     node.IsEditable &&
                     (node.Path.StartsWith("Prompt.", StringComparison.OrdinalIgnoreCase) ||
                      node.Path.StartsWith("Theme.Prompt.", StringComparison.OrdinalIgnoreCase))))
        {
            previewRuntime.ObjectAccessor.SetValue(previewRuntime.Config, leaf.Path, GetEffectiveValue(leaf));
        }

        return previewRuntime;
    }

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

    private object? GetCurrentValue(ConfigBrowserNode node)
    {
        return node.Path.Length == 0
            ? _runtime.Config
            : _runtime.ObjectAccessor.GetValue(_runtime.Config, node.Path);
    }

    private object? GetDefaultValue(ConfigBrowserNode node)
    {
        return node.Path.Length == 0
            ? _schema.DefaultConfig
            : _runtime.ObjectAccessor.GetValue(_schema.DefaultConfig, node.Path);
    }

    private ConfigBrowserNode? GetSelectedNode()
    {
        return _tree.TryGetSelected(out var selected) ? selected.Node : null;
    }

    private object? GetEffectiveValue(ConfigBrowserNode node)
    {
        return node.Path.Length > 0 && _stagedValues.TryGetValue(node.Path, out var stagedValue)
            ? stagedValue
            : GetCurrentValue(node);
    }

    private bool TryToggleSelectedBoolean()
    {
        var node = GetSelectedNode();

        if (node is null || node.Kind != ConfigBrowserNodeKind.Value || node.EditorKind != ConfigBrowserEditorKind.Boolean || !node.IsEditable)
        {
            return false;
        }

        var effectiveValue = GetEffectiveValue(node);
        var toggledValue = !(effectiveValue is bool boolean && boolean);
        StageValue(node.Path, toggledValue);
        _statusMessage = $"Staged {node.DisplayName} = {FormatValuePreview(toggledValue)}.";
        _focus = ConfigBrowserFocus.Detail;
        return true;
    }

    private bool BeginEditSelectedNode()
    {
        var node = GetSelectedNode();

        if (node is null)
        {
            return false;
        }

        _statusMessage = null;

        if (node.Kind == ConfigBrowserNodeKind.Group)
        {
            var editableChildren = GetGroupEditableChildren(node);

            if (editableChildren.Count == 0)
            {
                return false;
            }

            _groupEditingPath = node.Path;
            OpenGroupEditor(node, preferredKey: null);
            _editMode = ConfigBrowserEditMode.Group;
            _focus = ConfigBrowserFocus.Editor;
            _detailScroll.Home();
            return true;
        }

        if (node.Kind != ConfigBrowserNodeKind.Value || !node.IsEditable)
        {
            return false;
        }

        if (node.EditorKind == ConfigBrowserEditorKind.Boolean)
        {
            return TryToggleSelectedBoolean();
        }

        if (IsColorConfigNode(node) && TryBeginColorEdit(node))
        {
            return true;
        }

        if (IsPromptLayoutNode(node) && TryBeginPromptLayoutEdit(node))
        {
            return true;
        }

        if (node.EditorKind == ConfigBrowserEditorKind.Collection && TryBeginCollectionEdit(node))
        {
            return true;
        }

        if (node.EditorKind == ConfigBrowserEditorKind.Enum && TryBeginEnumEdit(node))
        {
            return true;
        }

        if (node.EditorKind is ConfigBrowserEditorKind.Text or ConfigBrowserEditorKind.Path or ConfigBrowserEditorKind.Number)
        {
            if (node.EditorKind == ConfigBrowserEditorKind.Path)
            {
                return BeginPathEditNode(node);
            }

            return BeginRawEditNode(node);
        }

        return false;
    }

    private bool BeginRawEditSelectedNode()
    {
        var node = GetSelectedNode();

        if (node is null)
        {
            return false;
        }

        return BeginRawEditNode(node);
    }

    private bool BeginRawEditNode(ConfigBrowserNode node)
    {
        if (node.Kind != ConfigBrowserNodeKind.Value ||
            !node.IsEditable ||
            node.EditorKind is not (ConfigBrowserEditorKind.Text or ConfigBrowserEditorKind.Path or ConfigBrowserEditorKind.Number))
        {
            return false;
        }

        _statusMessage = null;
        _textInput.SetText(GetEditableText(GetEffectiveValue(node)));
        _editingPath = node.Path;
        _editMode = ConfigBrowserEditMode.Text;
        _focus = ConfigBrowserFocus.Editor;
        _detailScroll.Home();
        return true;
    }

    private TuiScreenResult HandleEditorKey(ConsoleKeyInfo key)
    {
        if (_editMode == ConfigBrowserEditMode.Text)
        {
            var result = _textInput.HandleKey(key);

            switch (result)
            {
                case TuiTextInputResult.Submit:
                    CommitTextEdit();
                    break;
                case TuiTextInputResult.Cancel:
                    CancelEdit();
                    break;
                case TuiTextInputResult.Changed:
                    _statusMessage = null;
                    break;
            }

            return TuiScreenResult.Continue;
        }

        if (_editMode == ConfigBrowserEditMode.Path)
        {
            var result = _pathEditor.HandleKey(key, Math.Max(8, _detailScroll.PageSize));

            switch (result.Kind)
            {
                case TuiPathEditorActionKind.BrowseRequested:
                    BeginPathBrowse();
                    break;
                case TuiPathEditorActionKind.SubmitText:
                    CommitPathEdit();
                    break;
                case TuiPathEditorActionKind.Cancel:
                    CancelEdit();
                    break;
                case TuiPathEditorActionKind.TextChanged:
                    _statusMessage = null;
                    break;
                case TuiPathEditorActionKind.PickedPath:
                    CommitPickedPath(result.Path);
                    break;
                case TuiPathEditorActionKind.PickerClosed:
                    _statusMessage = "Closed filesystem picker.";
                    break;
            }

            return TuiScreenResult.Continue;
        }

        if (_editMode == ConfigBrowserEditMode.Enum)
        {
            switch (_enumPicker.HandleKey(key).Kind)
            {
                case TuiOptionPickerActionKind.Commit:
                    CommitEnumEdit();
                    break;
                case TuiOptionPickerActionKind.Cancel:
                    CancelEdit();
                    break;
            }

            return TuiScreenResult.Continue;
        }

        if (_editMode == ConfigBrowserEditMode.Color)
        {
            return HandleColorEditorKey(key);
        }

        if (_editMode == ConfigBrowserEditMode.Collection)
        {
            return HandleCollectionEditorKey(key);
        }

        if (_editMode == ConfigBrowserEditMode.Group)
        {
            return HandleGroupEditorKey(key);
        }

        if (_editMode == ConfigBrowserEditMode.PromptLayout)
        {
            return HandlePromptLayoutEditorKey(key);
        }

        return TuiScreenResult.Continue;
    }

    private TuiScreenResult HandleGroupEditorKey(ConsoleKeyInfo key)
    {
        var editingGroup = GetGroupEditingNode();

        if (editingGroup is null)
        {
            CancelEdit();
            return TuiScreenResult.Continue;
        }

        var editableChildren = GetGroupEditableChildren(editingGroup);

        if (editableChildren.Count == 0)
        {
            CancelEdit();
            return TuiScreenResult.Continue;
        }

        RefreshGroupEditor(editingGroup, _groupEditor.SelectedKey);
        var action = _groupEditor.HandleKey(key);

        switch (action.Kind)
        {
            case TuiGroupEditorActionKind.None:
                return TuiScreenResult.Continue;
            case TuiGroupEditorActionKind.SelectionUnavailable:
                _statusMessage = "There is no editable field selected.";
                return TuiScreenResult.Continue;
            case TuiGroupEditorActionKind.ToggleSelected when action.Item is not null && action.Item.EditorKind == ConfigBrowserEditorKind.Boolean:
                {
                    var toggledValue = !(GetEffectiveValue(action.Item) is bool enabled && enabled);
                    StageValue(action.Item.Path, toggledValue);
                    _statusMessage = $"Staged {action.Item.DisplayName} = {FormatValuePreview(toggledValue)}.";
                    return TuiScreenResult.Continue;
                }
            case TuiGroupEditorActionKind.ToggleSelected:
                _statusMessage = "The selected field is not a boolean toggle.";
                return TuiScreenResult.Continue;
            case TuiGroupEditorActionKind.EditSelected when action.Item is not null:
                BeginEditGroupChild(action.Item);
                return TuiScreenResult.Continue;
            case TuiGroupEditorActionKind.RawEditSelected when action.Item is not null:
                BeginRawEditNode(action.Item);
                return TuiScreenResult.Continue;
            case TuiGroupEditorActionKind.Close:
                CloseGroupEditor();
                return TuiScreenResult.Continue;
        }

        return TuiScreenResult.Continue;
    }

    private void RefreshGroupEditor(ConfigBrowserNode node, string? preferredKey)
    {
        var editableChildren = GetGroupEditableChildren(node);
        var pageSize = Math.Max(5, Math.Min(12, _detailScroll.PageSize > 0 ? _detailScroll.PageSize - 8 : 8));
        _groupEditor.Refresh(editableChildren, pageSize, preferredKey);
    }

    private void OpenGroupEditor(ConfigBrowserNode node, string? preferredKey)
    {
        var editableChildren = GetGroupEditableChildren(node);
        var pageSize = Math.Max(5, Math.Min(12, _detailScroll.PageSize > 0 ? _detailScroll.PageSize - 8 : 8));
        _groupEditor.Open(editableChildren, pageSize, child => child.Path, preferredKey);
    }

    private void RefreshCollectionEditor(ConfigBrowserNode node, string? preferredKey)
    {
        var items = ConfigCollectionEditorRegistry.GetItems(_runtime, node, GetEffectiveValue(node));
        var pageSize = Math.Max(5, Math.Min(12, _detailScroll.PageSize > 0 ? _detailScroll.PageSize - 8 : 8));
        _collectionEditor.Refresh(items, pageSize, preferredKey);
    }

    private void OpenCollectionEditor(ConfigBrowserNode node, string? preferredKey)
    {
        var items = ConfigCollectionEditorRegistry.GetItems(_runtime, node, GetEffectiveValue(node));
        var pageSize = Math.Max(5, Math.Min(12, _detailScroll.PageSize > 0 ? _detailScroll.PageSize - 8 : 8));
        _collectionEditor.Open(items, pageSize, item => item.Key, item => item.EditValue, preferredKey);
    }

    private TuiScreenResult HandleCollectionEditorKey(ConsoleKeyInfo key)
    {
        var editingNode = GetEditingNode();

        if (editingNode is null || editingNode.EditorKind != ConfigBrowserEditorKind.Collection)
        {
            CancelEdit();
            return TuiScreenResult.Continue;
        }

        RefreshCollectionEditor(editingNode, _collectionEditor.EditingItemKey);

        var action = _collectionEditor.HandleKey(key);

        switch (action.Kind)
        {
            case TuiCollectionEditorActionKind.None:
                _statusMessage = null;
                break;
            case TuiCollectionEditorActionKind.SubmitInput:
                CommitCollectionInput(editingNode, action);
                break;
            case TuiCollectionEditorActionKind.InputCancelled:
                CancelCollectionInput();
                break;
            case TuiCollectionEditorActionKind.RemoveItem:
                RemoveSelectedCollectionItem(editingNode, action.Key);
                break;
            case TuiCollectionEditorActionKind.EditUnavailable:
                _statusMessage = "There is no collection item to edit.";
                break;
            case TuiCollectionEditorActionKind.RemoveUnavailable:
                _statusMessage = "There is no collection item to remove.";
                break;
            case TuiCollectionEditorActionKind.Apply:
                ApplyStagedChanges();
                break;
            case TuiCollectionEditorActionKind.Save:
                SaveConfiguration();
                break;
            case TuiCollectionEditorActionKind.Close:
                CancelEdit();
                break;
        }

        return TuiScreenResult.Continue;
    }

    private void CommitCollectionInput(
        ConfigBrowserNode editingNode,
        TuiCollectionEditorAction<ConfigCollectionEditorItem> action)
    {
        object updatedValue;
        string status;
        bool updated;
        string? selectedKey = action.Key ?? _collectionEditor.EditingItemKey;

        if (action.InputMode == TuiCollectionEditorInputMode.AddItem)
        {
            updated = ConfigCollectionEditorRegistry.TryAddItem(
                _runtime,
                editingNode,
                GetEffectiveValue(editingNode),
                action.Text ?? string.Empty,
                out updatedValue,
                out status,
                out selectedKey);
        }
        else
        {
            if (action.Key is null)
            {
                _statusMessage = "There is no collection item selected to edit.";
                return;
            }

            updated = ConfigCollectionEditorRegistry.TryUpdateItem(
                _runtime,
                editingNode,
                GetEffectiveValue(editingNode),
                action.Key,
                action.Text ?? string.Empty,
                out updatedValue,
                out status);
        }

        if (!updated)
        {
            _statusMessage = status;
            return;
        }

        StageValue(editingNode.Path, updatedValue);
        _collectionEditor.CompleteInput(selectedKey);
        RefreshCollectionEditor(editingNode, selectedKey);
        _statusMessage = status;
    }

    private void CancelCollectionInput()
    {
        _collectionEditor.CancelInput();
        _statusMessage = "Cancelled collection edit.";
    }

    private void RemoveSelectedCollectionItem(ConfigBrowserNode editingNode, string? selectedKey)
    {
        if (string.IsNullOrWhiteSpace(selectedKey))
        {
            _statusMessage = "There is no collection item to remove.";
            return;
        }

        if (!ConfigCollectionEditorRegistry.TryRemoveItem(
                _runtime,
                editingNode,
                GetEffectiveValue(editingNode),
                selectedKey,
                out var updatedValue,
                out var status))
        {
            _statusMessage = status;
            return;
        }

        StageValue(editingNode.Path, updatedValue);
        _collectionEditor.CompleteInput(null);
        RefreshCollectionEditor(editingNode, preferredKey: null);
        _statusMessage = status;
    }

    private void BeginEditGroupChild(ConfigBrowserNode child)
    {
        if (!child.IsEditable)
        {
            return;
        }

        if (child.EditorKind == ConfigBrowserEditorKind.Collection && TryBeginCollectionEdit(child))
        {
            return;
        }

        if (child.EditorKind == ConfigBrowserEditorKind.Boolean)
        {
            var toggledValue = !(GetEffectiveValue(child) is bool enabled && enabled);
            StageValue(child.Path, toggledValue);
            _statusMessage = $"Staged {child.DisplayName} = {FormatValuePreview(toggledValue)}.";
            return;
        }

        if (IsColorConfigNode(child) && TryBeginColorEdit(child))
        {
            return;
        }

        if (IsPromptLayoutNode(child) && TryBeginPromptLayoutEdit(child))
        {
            return;
        }

        if (child.EditorKind == ConfigBrowserEditorKind.Enum && TryBeginEnumEdit(child))
        {
            return;
        }

        if (child.EditorKind is ConfigBrowserEditorKind.Text or ConfigBrowserEditorKind.Path or ConfigBrowserEditorKind.Number)
        {
            if (child.EditorKind == ConfigBrowserEditorKind.Path)
            {
                BeginPathEditNode(child);
                return;
            }

            _textInput.SetText(GetEditableText(GetEffectiveValue(child)));
            _editingPath = child.Path;
            _editMode = ConfigBrowserEditMode.Text;
            _focus = ConfigBrowserFocus.Editor;
            _detailScroll.Home();
        }
    }

    private bool BeginPathEditNode(ConfigBrowserNode node)
    {
        if (node.EditorKind != ConfigBrowserEditorKind.Path || !node.IsEditable)
        {
            return false;
        }

        _pathEditor.Open(GetEditableText(GetEffectiveValue(node)));
        _editingPath = node.Path;
        _editMode = ConfigBrowserEditMode.Path;
        _focus = ConfigBrowserFocus.Editor;
        _detailScroll.Home();
        return true;
    }

    private bool TryBeginEnumEdit(ConfigBrowserNode node)
    {
        if (node.EditorKind != ConfigBrowserEditorKind.Enum || !node.IsEditable)
        {
            return false;
        }

        var enumNames = Enum.GetNames(node.ValueType);
        var currentName = GetEffectiveValue(node)?.ToString();
        var preferredName = enumNames.FirstOrDefault(name => string.Equals(name, currentName, StringComparison.OrdinalIgnoreCase));
        _enumPicker.Open(enumNames, Math.Max(1, _detailScroll.PageSize > 0 ? _detailScroll.PageSize : 8), name => name, preferredName);
        _editingPath = node.Path;
        _editMode = ConfigBrowserEditMode.Enum;
        _focus = ConfigBrowserFocus.Editor;
        _detailScroll.Home();
        return true;
    }

    private bool TryBeginCollectionEdit(ConfigBrowserNode node)
    {
        if (node.EditorKind != ConfigBrowserEditorKind.Collection ||
            !node.IsEditable ||
            !ConfigCollectionEditorRegistry.SupportsEditing(node.Path, node.ValueType))
        {
            return false;
        }

        _collectionEditor.Close();
        _editingPath = node.Path;
        _editMode = ConfigBrowserEditMode.Collection;
        _focus = ConfigBrowserFocus.Editor;
        _statusMessage = null;
        OpenCollectionEditor(node, preferredKey: null);
        _detailScroll.Home();
        return true;
    }

    private void BeginPathBrowse()
    {
        var editingNode = GetEditingNode();

        if (editingNode is null || editingNode.EditorKind != ConfigBrowserEditorKind.Path)
        {
            return;
        }

        var pathInfo = DescribePathValue(editingNode, _pathEditor.Text);
        var startDirectory = Directory.Exists(pathInfo.ResolvedPath)
            ? pathInfo.ResolvedPath
            : Directory.Exists(Path.GetDirectoryName(pathInfo.ResolvedPath) ?? string.Empty)
                ? Path.GetDirectoryName(pathInfo.ResolvedPath)!
                : pathInfo.BaseDirectory;
        var initialSelectionPath = File.Exists(pathInfo.ResolvedPath) || Directory.Exists(pathInfo.ResolvedPath)
            ? pathInfo.ResolvedPath
            : null;

        _pathEditor.OpenPicker(
            startDirectory,
            GetPathPickerSelectionMode(editingNode),
            initialSelectionPath,
            Math.Max(8, _detailScroll.PageSize));
        _statusMessage = null;
    }

    private TuiFilePickerSelectionMode GetPathPickerSelectionMode(ConfigBrowserNode node)
    {
        return node.Name.Contains("Directory", StringComparison.OrdinalIgnoreCase)
            ? TuiFilePickerSelectionMode.Directory
            : TuiFilePickerSelectionMode.File;
    }

    private void CommitPickedPath(string? selectedPath)
    {
        var editingNode = GetEditingNode();

        if (editingNode is null || string.IsNullOrWhiteSpace(selectedPath))
        {
            CancelEdit();
            return;
        }

        var stagedText = FormatPickedPath(editingNode, selectedPath);
        _pathEditor.SetText(stagedText);

        object? convertedValue = stagedText;
        StageValue(editingNode.Path, convertedValue);
        _statusMessage = $"Staged {editingNode.DisplayName} = {FormatValuePreview(convertedValue)}.";
        _editingPath = null;
        RestoreParentEditorOrReturnToDetail();
    }

    private string FormatPickedPath(ConfigBrowserNode node, string selectedPath)
    {
        var baseDirectory = GetPathResolutionBaseDirectory(node);

        try
        {
            var relativePath = Path.GetRelativePath(baseDirectory, selectedPath);

            if (!string.IsNullOrWhiteSpace(relativePath) &&
                !relativePath.StartsWith("..", StringComparison.OrdinalIgnoreCase) &&
                !Path.IsPathRooted(relativePath))
            {
                return relativePath;
            }
        }
        catch
        {
        }

        return selectedPath;
    }

    private bool TryBeginColorEdit(ConfigBrowserNode node)
    {
        if (!node.IsEditable || !IsColorConfigNode(node))
        {
            return false;
        }

        var items = BuildColorEditorOptions(node);
        var preferredIndex = Math.Clamp(GetCurrentColorSelectionIndex(node), 0, Math.Max(0, items.Count - 1));
        var preferredKey = items.Count == 0 ? null : items[preferredIndex].Label;
        _colorPicker.Open(items, Math.Max(1, _detailScroll.PageSize > 0 ? _detailScroll.PageSize : 8), item => item.Label, preferredKey);
        _editingPath = node.Path;
        _editMode = ConfigBrowserEditMode.Color;
        _focus = ConfigBrowserFocus.Editor;
        _detailScroll.Home();
        return true;
    }

    private bool TryBeginPromptLayoutEdit(ConfigBrowserNode node)
    {
        if (!IsPromptLayoutNode(node) || GetEffectiveValue(node) is not string layoutText)
        {
            return false;
        }

        var unknownModules = ToshPromptRenderer.GetUnknownLayoutModules(layoutText);

        if (unknownModules.Count > 0)
        {
            return false;
        }

        CaptureLiveEditSnapshot(GetPromptLayoutSnapshotPaths(node.Path));
        _promptLayoutEditor.Open(
            CreatePromptLayoutEditorItems(layoutText),
            Math.Max(1, _detailScroll.PageSize > 0 ? _detailScroll.PageSize : 8),
            keySelector: item => item.Name,
            includedSelector: item => item.Included,
            includedUpdater: (item, included) => item with { Included = included },
            preferredKey: ToshPromptRenderer.GetLayoutModules(layoutText).FirstOrDefault(),
            minimumIncludedCount: 1);
        _editingPath = node.Path;
        _editMode = ConfigBrowserEditMode.PromptLayout;
        _focus = ConfigBrowserFocus.Editor;
        _detailScroll.Home();
        return true;
    }

    private TuiScreenResult HandlePromptLayoutEditorKey(ConsoleKeyInfo key)
    {
        var editingNode = GetEditingNode();

        if (editingNode is null || !IsPromptLayoutNode(editingNode) || _promptLayoutEditor.Items.Count == 0)
        {
            ClosePromptLayoutEditor(restoreSnapshot: true);
            return TuiScreenResult.Continue;
        }

        switch (_promptLayoutEditor.HandleKey(key).Kind)
        {
            case TuiOrderedToggleEditorActionKind.Toggled:
            case TuiOrderedToggleEditorActionKind.Reordered:
                StagePromptLayoutPreview(editingNode);
                return TuiScreenResult.Continue;
            case TuiOrderedToggleEditorActionKind.ToggleRejected:
                _statusMessage = "Prompt layouts must include at least one module.";
                return TuiScreenResult.Continue;
            case TuiOrderedToggleEditorActionKind.Commit:
                {
                    var stagedLayout = BuildPromptLayoutString();
                    ClosePromptLayoutEditor(restoreSnapshot: false);
                    _statusMessage = $"Staged {editingNode.DisplayName} = {FormatValuePreview(stagedLayout)}.";
                    return TuiScreenResult.Continue;
                }
            case TuiOrderedToggleEditorActionKind.Cancel:
                ClosePromptLayoutEditor(restoreSnapshot: true);
                _statusMessage = $"Cancelled {editingNode.DisplayName} edit.";
                return TuiScreenResult.Continue;
        }

        return TuiScreenResult.Continue;
    }

    private TuiScreenResult HandleColorEditorKey(ConsoleKeyInfo key)
    {
        if (GetEditingNode() is not { } editingNode || !IsColorConfigNode(editingNode) || _colorPicker.Items.Count == 0)
        {
            CancelEdit();
            return TuiScreenResult.Continue;
        }

        switch (_colorPicker.HandleKey(key).Kind)
        {
            case TuiOptionPickerActionKind.Commit:
                CommitColorEdit();
                return TuiScreenResult.Continue;
            case TuiOptionPickerActionKind.Cancel:
                CancelEdit();
                return TuiScreenResult.Continue;
        }

        return TuiScreenResult.Continue;
    }

    private void StagePromptLayoutPreview(ConfigBrowserNode editingNode)
    {
        var layoutText = BuildPromptLayoutString();
        StageValue(editingNode.Path, layoutText);
        var autoEnabledModules = StagePromptModulesForLayout(editingNode, layoutText).ToArray();
        _statusMessage = autoEnabledModules.Length == 0
            ? $"Staged {editingNode.DisplayName} = {FormatValuePreview(layoutText)}."
            : $"Staged {editingNode.DisplayName} = {FormatValuePreview(layoutText)}. Auto-enabled: {string.Join(", ", autoEnabledModules)}.";
    }

    private string BuildPromptLayoutString()
    {
        return string.Join(", ", _promptLayoutEditor.Items
            .Where(item => item.Included)
            .Select(item => item.Name));
    }

    private void CaptureLiveEditSnapshot(IEnumerable<string> paths)
    {
        _liveEditSnapshot.Clear();

        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _liveEditSnapshot.Add(_stagedValues.TryGetValue(path, out var stagedValue)
                ? new ConfigEditSnapshotEntry(path, WasStaged: true, stagedValue)
                : new ConfigEditSnapshotEntry(path, WasStaged: false, Value: null));
        }
    }

    private void RestoreLiveEditSnapshot()
    {
        foreach (var snapshotEntry in _liveEditSnapshot)
        {
            if (snapshotEntry.WasStaged)
            {
                _stagedValues[snapshotEntry.Path] = snapshotEntry.Value;
            }
            else
            {
                _stagedValues.Remove(snapshotEntry.Path);
            }
        }

        SyncTree(_tree.Scroll.PageSize > 0 ? _tree.Scroll.PageSize : 10);
        _detailScroll.Home();
    }

    private IEnumerable<string> GetPromptLayoutSnapshotPaths(string layoutPath)
    {
        yield return layoutPath;

        foreach (var module in ToshPromptRenderer.SupportedModuleNames)
        {
            var enabledPath = GetPromptModuleEnabledPath(module);

            if (enabledPath is not null)
            {
                yield return enabledPath;
            }
        }
    }

    private void ClosePromptLayoutEditor(bool restoreSnapshot)
    {
        if (restoreSnapshot)
        {
            RestoreLiveEditSnapshot();
        }

        _promptLayoutEditor.Close();
        _liveEditSnapshot.Clear();
        _editingPath = null;
        RestoreParentEditorOrReturnToDetail();
    }

    private static List<PromptLayoutEditorItem> CreatePromptLayoutEditorItems(string layoutText)
    {
        var includedModules = ToshPromptRenderer.GetLayoutModules(layoutText);
        var items = includedModules
            .Select(module => new PromptLayoutEditorItem(module, Included: true))
            .ToList();

        foreach (var supportedModule in ToshPromptRenderer.SupportedModuleNames)
        {
            if (includedModules.Contains(supportedModule, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            items.Add(new PromptLayoutEditorItem(supportedModule, Included: false));
        }

        return items;
    }

    private void CommitTextEdit()
    {
        var editingNode = GetEditingNode();

        if (editingNode is null)
        {
            CancelEdit();
            return;
        }

        var rawText = _textInput.Text;
        object? convertedValue;

        if (editingNode.IsNullable &&
            editingNode.ValueType != typeof(string) &&
            string.IsNullOrWhiteSpace(rawText))
        {
            convertedValue = null;
        }
        else if (editingNode.ValueType == typeof(string))
        {
            convertedValue = rawText;
        }
        else if (!TypeConversion.TryConvert(rawText, editingNode.ValueType, out convertedValue))
        {
            _statusMessage = $"Could not convert {FormatValuePreview(rawText)} to {editingNode.TypeName}.";
            return;
        }

        StageValue(editingNode.Path, convertedValue);
        var autoEnabledModules = StagePromptModulesForLayout(editingNode, convertedValue).ToArray();
        _statusMessage = autoEnabledModules.Length == 0
            ? $"Staged {editingNode.DisplayName} = {FormatValuePreview(convertedValue)}."
            : $"Staged {editingNode.DisplayName} = {FormatValuePreview(convertedValue)}. Auto-enabled: {string.Join(", ", autoEnabledModules)}.";
        _editingPath = null;
        RestoreParentEditorOrReturnToDetail();
    }

    private IEnumerable<string> StagePromptModulesForLayout(ConfigBrowserNode node, object? convertedValue)
    {
        if (!IsPromptLayoutNode(node) || convertedValue is not string layoutText)
        {
            yield break;
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

            StageValue(enabledPath, true);
            yield return module;
        }
    }

    private void CommitEnumEdit()
    {
        var editingNode = GetEditingNode();

        if (editingNode is null)
        {
            CancelEdit();
            return;
        }

        if (!_enumPicker.TryGetSelected(out var selectedName))
        {
            CancelEdit();
            return;
        }
        var convertedValue = Enum.Parse(editingNode.ValueType, selectedName, ignoreCase: true);
        StageValue(editingNode.Path, convertedValue);
        _statusMessage = $"Staged {editingNode.DisplayName} = {selectedName}.";
        _editingPath = null;
        RestoreParentEditorOrReturnToDetail();
    }

    private void CommitColorEdit()
    {
        var editingNode = GetEditingNode();

        if (editingNode is null || !_colorPicker.TryGetSelected(out var selectedOption))
        {
            CancelEdit();
            return;
        }

        StageValue(editingNode.Path, selectedOption.Value);
        _statusMessage = $"Staged {editingNode.DisplayName} = {FormatValuePreview(selectedOption.Value)}.";
        _editingPath = null;
        RestoreParentEditorOrReturnToDetail();
    }

    private void CommitPathEdit()
    {
        var editingNode = GetEditingNode();

        if (editingNode is null)
        {
            CancelEdit();
            return;
        }

        object? convertedValue = _pathEditor.Text;
        StageValue(editingNode.Path, convertedValue);
        _statusMessage = $"Staged {editingNode.DisplayName} = {FormatValuePreview(convertedValue)}.";
        _editingPath = null;
        RestoreParentEditorOrReturnToDetail();
    }

    private void CancelEdit()
    {
        if (_editMode == ConfigBrowserEditMode.PromptLayout)
        {
            ClosePromptLayoutEditor(restoreSnapshot: true);
            return;
        }

        _pathEditor.Close();
        _collectionEditor.Close();
        _colorPicker.Close();
        _enumPicker.Close();
        _editingPath = null;

        if (_groupEditingPath is not null)
        {
            _editMode = ConfigBrowserEditMode.Group;
            _focus = ConfigBrowserFocus.Editor;
            return;
        }

        _editMode = ConfigBrowserEditMode.None;
        _focus = ConfigBrowserFocus.Detail;
    }

    private void CloseGroupEditor()
    {
        _promptLayoutEditor.Close();
        _liveEditSnapshot.Clear();
        _pathEditor.Close();
        _collectionEditor.Close();
        _groupEditor.Close();
        _colorPicker.Close();
        _enumPicker.Close();
        _editingPath = null;
        _groupEditingPath = null;
        _editMode = ConfigBrowserEditMode.None;
        _focus = ConfigBrowserFocus.Detail;
    }

    private bool ApplyStagedChanges()
    {
        if (_stagedValues.Count == 0)
        {
            _statusMessage = "No staged changes to apply.";
            return true;
        }

        try
        {
            var appliedCount = ApplyStagedValuesToRuntime();
            _stagedValues.Clear();
            SyncTree(_tree.Scroll.PageSize > 0 ? _tree.Scroll.PageSize : 10);
            _statusMessage = $"Applied {appliedCount} staged change{(appliedCount == 1 ? string.Empty : "s")}.";
            return true;
        }
        catch (Exception ex)
        {
            _statusMessage = $"Apply failed: {ex.Message}";
            return true;
        }
    }

    private bool TryReloadStartupAction()
    {
        var node = GetSelectedNode();

        if (node is null || !ShouldShowStartupActions(node))
        {
            return false;
        }

        if (_stagedValues.Count > 0)
        {
            _pendingConfirmAction = ConfigBrowserConfirmAction.ReloadStartup;
            _confirmDialog.Open(
                "Discard Staged Changes?",
                $"You have {_stagedValues.Count} staged change{(_stagedValues.Count == 1 ? string.Empty : "s")}. Reloading will discard them and re-run startup files. Continue?",
                confirmLabel: "Reload",
                cancelLabel: "Stay");
            _focus = ConfigBrowserFocus.Detail;
            _statusMessage = null;
            return true;
        }

        ExecuteReloadStartupAction();
        return true;
    }

    private TuiScreenResult ExecuteReloadStartupAction()
    {
        try
        {
            _stagedValues.Clear();
            _pathEditor.Close();
            _promptLayoutEditor.Close();
            _liveEditSnapshot.Clear();
            _collectionEditor.Close();
            _groupEditor.Close();
            _colorPicker.Close();
            _enumPicker.Close();
            _editingPath = null;
            _groupEditingPath = null;
            _editMode = ConfigBrowserEditMode.None;
            _focus = ConfigBrowserFocus.Detail;

            var reload = ConfigStartupUtilities.ReloadConfigurationAsync(_runtime)
                .GetAwaiter()
                .GetResult();

            SyncTree(_tree.Scroll.PageSize > 0 ? _tree.Scroll.PageSize : 10);
            _detailScroll.Home();
            _statusMessage = $"Reloaded {reload.LoadedPaths.Count} startup file{(reload.LoadedPaths.Count == 1 ? string.Empty : "s")} from {reload.RootDirectory}.";
        }
        catch (InvalidOperationException)
        {
            _statusMessage = "Configuration reload is not available in this session.";
        }
        catch (Exception ex)
        {
            _statusMessage = $"Reload failed: {ex.Message}";
        }

        return TuiScreenResult.Continue;
    }

    private bool TryInitializeStartupAction()
    {
        var node = GetSelectedNode();

        if (node is null || !ShouldShowStartupActions(node))
        {
            return false;
        }

        ExecuteInitializeStartupAction();
        return true;
    }

    private TuiScreenResult ExecuteInitializeStartupAction()
    {
        try
        {
            var init = ConfigStartupUtilities.InitializeConfigDirectory(_runtime.Config.Startup.RootDirectory);
            _detailScroll.Home();
            _statusMessage = init.CreatedPaths.Count == 0
                ? $"Startup layout is already initialized at {init.RootDirectory}."
                : $"Initialized {init.CreatedPaths.Count} startup path{(init.CreatedPaths.Count == 1 ? string.Empty : "s")} at {init.RootDirectory}.";
        }
        catch (Exception ex)
        {
            _statusMessage = $"Initialization failed: {ex.Message}";
        }

        return TuiScreenResult.Continue;
    }

    private bool SaveConfiguration()
    {
        try
        {
            var appliedCount = _stagedValues.Count > 0
                ? ApplyStagedValuesToRuntime()
                : 0;

            var configPath = _runtime.Config.Startup.ResolvePath(_runtime.Config.Startup.ConfigFilePath);
            Directory.CreateDirectory(Path.GetDirectoryName(configPath) ?? _runtime.Config.Startup.RootDirectory);

            var existingText = File.Exists(configPath)
                ? File.ReadAllText(configPath)
                : string.Empty;
            var updatedText = UpsertManagedConfigBlock(existingText, BuildManagedConfigBlockText());
            File.WriteAllText(configPath, updatedText);

            if (appliedCount > 0)
            {
                _stagedValues.Clear();
            }

            SyncTree(_tree.Scroll.PageSize > 0 ? _tree.Scroll.PageSize : 10);
            _detailScroll.Home();
            _statusMessage = appliedCount > 0
                ? $"Applied {appliedCount} staged change{(appliedCount == 1 ? string.Empty : "s")} and saved them to {configPath}."
                : $"Saved configuration to {configPath}.";
            return true;
        }
        catch (Exception ex)
        {
            _statusMessage = _stagedValues.Count > 0
                ? $"Save failed after applying staged changes to the live session: {ex.Message}"
                : $"Save failed: {ex.Message}";
            return true;
        }
    }

    private int ApplyStagedValuesToRuntime()
    {
        foreach (var stagedEntry in _stagedValues.OrderBy(entry => entry.Key.Count(character => character == '.')))
        {
            if (_schema.NodesByPath.TryGetValue(stagedEntry.Key, out var node) &&
                node.EditorKind == ConfigBrowserEditorKind.Collection &&
                ConfigCollectionEditorRegistry.SupportsEditing(node.Path, node.ValueType))
            {
                if (!ConfigCollectionEditorRegistry.TryApplyValue(_runtime, node, stagedEntry.Value, out var errorMessage))
                {
                    throw new InvalidOperationException(errorMessage);
                }

                continue;
            }

            _runtime.ObjectAccessor.SetValue(_runtime.Config, stagedEntry.Key, stagedEntry.Value);
        }

        return _stagedValues.Count;
    }

    private bool RevertSelectedNode()
    {
        var node = GetSelectedNode();

        if (node is null)
        {
            return false;
        }

        var removedKeys = _stagedValues.Keys
            .Where(path => IsPathWithinNode(path, node.Path))
            .ToArray();

        foreach (var removedKey in removedKeys)
        {
            _stagedValues.Remove(removedKey);
        }

        if (_editMode == ConfigBrowserEditMode.PromptLayout && _editingPath is not null && IsPathWithinNode(_editingPath, node.Path))
        {
            ClosePromptLayoutEditor(restoreSnapshot: false);
        }
        else if (_editingPath is not null && IsPathWithinNode(_editingPath, node.Path))
        {
            CancelEdit();
        }

        if (_groupEditingPath is not null && IsPathWithinNode(_groupEditingPath, node.Path))
        {
            _groupEditor.Close();
            _groupEditingPath = null;
            _editMode = ConfigBrowserEditMode.None;
            _focus = ConfigBrowserFocus.Detail;
        }

        SyncTree(_tree.Scroll.PageSize > 0 ? _tree.Scroll.PageSize : 10);
        _detailScroll.Home();
        _statusMessage = removedKeys.Length == 0
            ? "No staged changes to revert for the selected node."
            : $"Reverted {removedKeys.Length} staged change{(removedKeys.Length == 1 ? string.Empty : "s")}.";
        return true;
    }

    private bool ResetSelectedNodeToDefaults()
    {
        var node = GetSelectedNode();

        if (node is null)
        {
            return false;
        }

        var editableLeaves = EnumerateLeafNodes(node)
            .Where(leaf => leaf.IsEditable)
            .ToArray();

        if (editableLeaves.Length == 0)
        {
            _statusMessage = "The selected node does not contain editable values.";
            return true;
        }

        foreach (var leaf in editableLeaves)
        {
            SetStagedValue(leaf.Path, GetDefaultValue(leaf));
        }

        if (_editMode == ConfigBrowserEditMode.PromptLayout && _editingPath is not null && IsPathWithinNode(_editingPath, node.Path))
        {
            ClosePromptLayoutEditor(restoreSnapshot: false);
        }
        else if (_editingPath is not null && IsPathWithinNode(_editingPath, node.Path))
        {
            CancelEdit();
        }

        if (_groupEditingPath is not null && IsPathWithinNode(_groupEditingPath, node.Path))
        {
            RefreshGroupEditor(GetGroupEditingNode() ?? node, preferredKey: null);
        }

        SyncTree(_tree.Scroll.PageSize > 0 ? _tree.Scroll.PageSize : 10);
        _detailScroll.Home();
        _statusMessage = $"Staged default values for {editableLeaves.Length} setting{(editableLeaves.Length == 1 ? string.Empty : "s")}.";
        return true;
    }

    private IEnumerable<ConfigBrowserNode> EnumerateLeafNodes(ConfigBrowserNode node)
    {
        if (node.Kind == ConfigBrowserNodeKind.Value)
        {
            yield return node;
            yield break;
        }

        foreach (var child in node.Children)
        {
            foreach (var descendant in EnumerateLeafNodes(child))
            {
                yield return descendant;
            }
        }
    }

    private ConfigBrowserNode? GetEditingNode()
    {
        return _editingPath is not null && _schema.NodesByPath.TryGetValue(_editingPath, out var node)
            ? node
            : null;
    }

    private int CountStagedChanges(ConfigBrowserNode node)
    {
        return node.Path.Length == 0
            ? _stagedValues.Count
            : _stagedValues.Keys.Count(path => IsPathWithinNode(path, node.Path));
    }

    private bool HasStagedChanges(ConfigBrowserNode node)
    {
        return node.Path.Length == 0
            ? _stagedValues.Count > 0
            : _stagedValues.Keys.Any(path => IsPathWithinNode(path, node.Path));
    }

    private bool SetStagedValue(string path, object? value)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var currentValue = _runtime.ObjectAccessor.GetValue(_runtime.Config, path);

        if (ValuesEqual(currentValue, value))
        {
            return _stagedValues.Remove(path);
        }

        _stagedValues[path] = value;
        return true;
    }

    private void StageValue(string path, object? value)
    {
        SetStagedValue(path, value);
        SyncTree(_tree.Scroll.PageSize > 0 ? _tree.Scroll.PageSize : 10);
        _detailScroll.Home();
    }

    private static bool IsPathWithinNode(string candidatePath, string nodePath)
    {
        return nodePath.Length == 0 ||
               string.Equals(candidatePath, nodePath, StringComparison.OrdinalIgnoreCase) ||
               candidatePath.StartsWith(nodePath + ".", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetEditableText(object? value)
    {
        return value switch
        {
            null => string.Empty,
            string text => text,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => value.ToString() ?? string.Empty,
        };
    }

    private bool ExpandSelectedGroup()
    {
        if (!_tree.TryGetSelected(out var selected) || selected.Node.Kind != ConfigBrowserNodeKind.Group)
        {
            return false;
        }

        if (_expandedPaths.Add(selected.Node.Path))
        {
            SyncTree(_tree.Scroll.PageSize > 0 ? _tree.Scroll.PageSize : 10);
        }

        return true;
    }

    private bool CollapseSelectedGroup()
    {
        if (!_tree.TryGetSelected(out var selected))
        {
            return false;
        }

        if (selected.Node.Kind == ConfigBrowserNodeKind.Group && _expandedPaths.Remove(selected.Node.Path))
        {
            SyncTree(_tree.Scroll.PageSize > 0 ? _tree.Scroll.PageSize : 10);
            return true;
        }

        var parentPath = GetParentPath(selected.Node.Path);

        if (parentPath is null)
        {
            return false;
        }

        _selectedPath = parentPath;
        SyncTree(_tree.Scroll.PageSize > 0 ? _tree.Scroll.PageSize : 10);
        return true;
    }

    private void ToggleExpanded(string path)
    {
        if (!_expandedPaths.Add(path))
        {
            _expandedPaths.Remove(path);
        }

        _selectedPath = path;
        SyncTree(_tree.Scroll.PageSize > 0 ? _tree.Scroll.PageSize : 10);
    }

    private void ExpandAncestors(string path)
    {
        var current = path;

        while (!string.IsNullOrWhiteSpace(current))
        {
            var parent = GetParentPath(current);

            if (parent is null)
            {
                break;
            }

            _expandedPaths.Add(parent);
            current = parent;
        }
    }

    private void CycleFocus(bool reverse)
    {
        if (_editMode != ConfigBrowserEditMode.None)
        {
            return;
        }

        _focus = reverse
            ? _focus switch
            {
                ConfigBrowserFocus.Search => ConfigBrowserFocus.Detail,
                ConfigBrowserFocus.Tree => ConfigBrowserFocus.Search,
                ConfigBrowserFocus.Detail => ConfigBrowserFocus.Tree,
                _ => ConfigBrowserFocus.Detail,
            }
            : _focus switch
            {
                ConfigBrowserFocus.Search => ConfigBrowserFocus.Tree,
                ConfigBrowserFocus.Tree => ConfigBrowserFocus.Detail,
                ConfigBrowserFocus.Detail => ConfigBrowserFocus.Search,
                _ => ConfigBrowserFocus.Detail,
            };
    }

    private string RenderSearchBox(int width)
    {
        var label = _focus == ConfigBrowserFocus.Search ? "Search*" : "Search";
        var theme = _runtime.Config.Theme.Tui;
        var box = TuiBoxDrawing.GetBoxCharacters(theme.BoxStyle);
        var builder = new StringBuilder();
        builder.Append(TuiRenderHelpers.RenderTopBorder(width, "Config Browser", theme, box));
        builder.AppendLine(TuiRenderHelpers.RenderSearchRow(label, _query, width, theme, box));
        builder.Append(TuiRenderHelpers.RenderBottomBorder(width, theme, box));
        return builder.ToString();
    }

    private string RenderContentRows(TuiRect treeRect, TuiRect detailRect, IReadOnlyList<ConfigDetailEntry> detailEntries)
    {
        var theme = _runtime.Config.Theme.Tui;
        var box = TuiBoxDrawing.GetBoxCharacters(theme.BoxStyle);
        var detailTitle = _confirmDialog.IsOpen
            ? _confirmDialog.Title
            : _pathEditor.IsBrowsing
                ? "Filesystem Picker"
                : _tree.TryGetSelected(out var selected)
                    ? selected.Node.DisplayName
                    : "Details";

        return TuiRenderHelpers.RenderDualPaneContent(
            treeRect,
            detailRect,
            "Configuration",
            detailTitle,
            _tree.Scroll.GetVisibleRange(),
            _detailScroll.GetVisibleRange(),
            _tree.SelectedIndex,
            (itemIndex, isSelected) => RenderTreeLine(_tree.Items[itemIndex], isSelected, treeRect.Width, theme, box),
            entryIndex =>
            {
                var entry = detailEntries[entryIndex];
                return TuiRenderHelpers.RenderBoxContentLine(entry.Text, detailRect.Width, GetDetailStyle(entry.Kind, theme), theme, box);
            },
            theme,
            box);
    }

    private string RenderTreeLine(
        ConfigBrowserListEntry item,
        bool isSelected,
        int width,
        ToshTuiThemeConfig theme,
        TuiBoxCharacters box)
    {
        var label = item.Label;
        var style = item.Node.Kind == ConfigBrowserNodeKind.Group
            ? TuiRenderHelpers.MergeListStyles(theme.SectionHeading, theme.SelectedItem, isSelected, preserveForeground: true)
            : TuiRenderHelpers.MergeListStyles(theme.ListItem, theme.SelectedItem, isSelected, preserveForeground: false);

        return TuiRenderHelpers.RenderBoxContentLine(label, width, style, theme, box);
    }

    private string RenderFooter(int width)
    {
        var focus = _focus.ToString().ToLowerInvariant();
        var theme = _runtime.Config.Theme.Tui;
        var dirtyCount = _stagedValues.Count;
        var selectedNode = GetSelectedNode();
        var validationSummary = TuiValidationFormatter.BuildSummary(
            selectedNode is not null
                ? GetValidationMessages(selectedNode).ToArray()
                : Array.Empty<TuiValidationMessage>());
        var startupHint = selectedNode is not null && ShouldShowStartupActions(selectedNode)
            ? "  l reload  i init"
            : string.Empty;
        var text = _confirmDialog.IsOpen
            ? $"focus:{focus}  dirty:{dirtyCount}  Left/Right choose  Enter confirm  Esc cancel"
            : _editMode switch
            {
                ConfigBrowserEditMode.Text => $"focus:{focus}  dirty:{dirtyCount}  editing text  Enter stage  Esc cancel",
                ConfigBrowserEditMode.Path when _pathEditor.IsBrowsing => $"focus:{focus}  dirty:{dirtyCount}  browsing paths  Enter open/select  Space pick  Left parent  Esc close",
                ConfigBrowserEditMode.Path => $"focus:{focus}  dirty:{dirtyCount}  editing path  b browse  Enter stage  Esc cancel",
                ConfigBrowserEditMode.Enum => $"focus:{focus}  dirty:{dirtyCount}  editing enum  Up/Down pick  Enter stage  Esc cancel",
                ConfigBrowserEditMode.Color => $"focus:{focus}  dirty:{dirtyCount}  editing color  Up/Down pick  Enter stage  Esc cancel",
                ConfigBrowserEditMode.Collection => _collectionEditor.InputMode == TuiCollectionEditorInputMode.None
                    ? $"focus:{focus}  dirty:{dirtyCount}  editing collection  Enter edit  n add  Del remove  a apply  s save  Esc close"
                    : $"focus:{focus}  dirty:{dirtyCount}  editing collection item  Enter stage  Esc cancel",
                ConfigBrowserEditMode.PromptLayout => $"focus:{focus}  dirty:{dirtyCount}  editing prompt layout  Space toggle  Shift+Up/Down reorder  Enter keep  Esc restore",
                ConfigBrowserEditMode.Group => $"focus:{focus}  dirty:{dirtyCount}  editing group  Up/Down select  Enter edit  Space toggle  Esc close",
                _ => $"focus:{focus}  dirty:{dirtyCount}  {validationSummary}  / search  Enter expand/open  Tab switch panes  e edit  t raw-edit  Space toggle  a apply  s save  r revert  R reset{startupHint}  q quit"
            };
        return TuiRenderHelpers.RenderFooterLine(text, width, theme);
    }

    private IReadOnlyList<ConfigBrowserNode> GetGroupEditableChildren(ConfigBrowserNode node)
    {
        return node.Children
            .Where(child => child.IsEditable)
            .ToArray();
    }

    private ConfigBrowserNode? GetActiveGroupChildEditorNode(ConfigBrowserNode groupNode)
    {
        var editingNode = GetEditingNode();

        if (editingNode is null ||
            string.Equals(editingNode.Path, groupNode.Path, StringComparison.OrdinalIgnoreCase) ||
            !IsPathWithinNode(editingNode.Path, groupNode.Path))
        {
            return null;
        }

        return editingNode;
    }

    private ConfigBrowserNode? GetGroupEditingNode()
    {
        return _groupEditingPath is not null && _schema.NodesByPath.TryGetValue(_groupEditingPath, out var node)
            ? node
            : null;
    }

    private static string GetGroupEditorHeading(ConfigBrowserNode node)
    {
        if (node.ValueType == typeof(ToshTextStyleConfig))
        {
            return "Style Editor";
        }

        if (node.Path.Equals("Prompt", StringComparison.OrdinalIgnoreCase))
        {
            return "Prompt Editor";
        }

        return "Section Editor";
    }

    private string GetGroupEditorMarker(ConfigBrowserNode node)
    {
        if (node.EditorKind == ConfigBrowserEditorKind.Boolean)
        {
            return GetEffectiveValue(node) is bool enabled && enabled ? "[x]" : "[ ]";
        }

        if (IsPromptLayoutNode(node))
        {
            return "[#]";
        }

        return node.EditorKind switch
        {
            ConfigBrowserEditorKind.Enum => "[@]",
            ConfigBrowserEditorKind.Number => "[#]",
            ConfigBrowserEditorKind.Path => "[/]",
            ConfigBrowserEditorKind.Collection => "[=]",
            _ => " - ",
        };
    }

    private string FormatGroupEditorValue(ConfigBrowserNode node)
    {
        if (IsPromptLayoutNode(node) && GetEffectiveValue(node) is string layoutText)
        {
            return layoutText.Length == 0 ? "<none>" : layoutText;
        }

        if (node.EditorKind == ConfigBrowserEditorKind.Boolean && GetEffectiveValue(node) is bool enabled)
        {
            return enabled ? "enabled" : "disabled";
        }

        return FormatValuePreview(GetEffectiveValue(node));
    }

    private void RestoreParentEditorOrReturnToDetail()
    {
        if (_groupEditingPath is not null)
        {
            _editMode = ConfigBrowserEditMode.Group;
            _focus = ConfigBrowserFocus.Editor;
            return;
        }

        _collectionEditor.Close();
        _editMode = ConfigBrowserEditMode.None;
        _focus = ConfigBrowserFocus.Detail;
    }

    private string BuildManagedConfigBlockText()
    {
        var assignments = new List<string>();

        foreach (var node in EnumerateLeafNodes(_schema.Root)
                     .Where(node => node.IsEditable)
                     .OrderBy(node => node.Path, StringComparer.OrdinalIgnoreCase))
        {
            var current = GetCurrentValue(node);
            var defaultValue = GetDefaultValue(node);

            if (ValuesEqual(current, defaultValue))
            {
                continue;
            }

            if (node.EditorKind == ConfigBrowserEditorKind.Collection &&
                ConfigCollectionEditorRegistry.SupportsEditing(node.Path, node.ValueType))
            {
                assignments.AddRange(ConfigCollectionEditorRegistry.BuildManagedConfigLines(_runtime, node, current, QuoteConfigString));
                continue;
            }

            assignments.Add($"$tosh.Config.{node.Path} = {FormatConfigLiteral(current)}");
        }

        var lines = new List<string>
        {
            ManagedConfigBlockStart,
            "# Generated by config browse. Manual edits inside this block may be replaced."
        };

        lines.AddRange(assignments);
        lines.Add(ManagedConfigBlockEnd);
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static string UpsertManagedConfigBlock(string existingText, string managedBlock)
    {
        if (string.IsNullOrEmpty(existingText))
        {
            return managedBlock;
        }

        var startIndex = existingText.IndexOf(ManagedConfigBlockStart, StringComparison.Ordinal);
        var endIndex = existingText.IndexOf(ManagedConfigBlockEnd, StringComparison.Ordinal);

        if (startIndex >= 0 && endIndex >= startIndex)
        {
            var afterEnd = endIndex + ManagedConfigBlockEnd.Length;

            while (afterEnd < existingText.Length &&
                   (existingText[afterEnd] == '\r' || existingText[afterEnd] == '\n'))
            {
                afterEnd++;
            }

            return existingText[..startIndex] + managedBlock + existingText[afterEnd..];
        }

        var builder = new StringBuilder(existingText);

        if (!existingText.EndsWith(Environment.NewLine, StringComparison.Ordinal))
        {
            builder.AppendLine();
        }

        builder.AppendLine();
        builder.Append(managedBlock);
        return builder.ToString();
    }

    private static string FormatConfigLiteral(object? value)
    {
        return value switch
        {
            null => "null",
            string text => QuoteConfigString(text),
            bool boolean => boolean ? "true" : "false",
            Enum @enum => QuoteConfigString(@enum.ToString() ?? string.Empty),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? QuoteConfigString(value.ToString() ?? string.Empty),
            _ => QuoteConfigString(value.ToString() ?? string.Empty),
        };
    }

    private static string QuoteConfigString(string text)
    {
        var builder = new StringBuilder(text.Length + 2);
        builder.Append('"');

        foreach (var character in text)
        {
            builder.Append(character switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => character.ToString(),
            });
        }

        builder.Append('"');
        return builder.ToString();
    }

    private static ToshTextStyleConfig GetDetailStyle(ConfigDetailEntryKind kind, ToshTuiThemeConfig theme)
    {
        return kind switch
        {
            ConfigDetailEntryKind.SectionHeading => theme.SectionHeading,
            ConfigDetailEntryKind.Meta => theme.Meta,
            ConfigDetailEntryKind.Preview => new ToshTextStyleConfig(),
            _ => theme.DetailText,
        };
    }

    private static string? GetParentPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var separator = path.LastIndexOf('.');
        return separator < 0 ? string.Empty : path[..separator];
    }

    private static bool ValuesEqual(object? left, object? right)
    {
        if (left is System.Collections.IEnumerable leftEnumerable &&
            right is System.Collections.IEnumerable rightEnumerable &&
            left is not string &&
            right is not string)
        {
            return leftEnumerable.Cast<object?>().SequenceEqual(rightEnumerable.Cast<object?>());
        }

        return Equals(left, right);
    }

    private static string? GetPromptModuleEnabledPath(string module)
    {
        return module switch
        {
            "Time" => "Prompt.TimeEnabled",
            "Git" => "Prompt.GitEnabled",
            "UserHost" => "Prompt.UserHostEnabled",
            "HistoryId" => "Prompt.HistoryIdEnabled",
            "Jobs" => "Prompt.JobsEnabled",
            "Duration" => "Prompt.DurationEnabled",
            "ExitCode" => "Prompt.ExitCodeEnabled",
            _ => null,
        };
    }

    private static string FormatEditorKind(ConfigBrowserEditorKind kind)
    {
        return kind switch
        {
            ConfigBrowserEditorKind.Boolean => "Boolean",
            ConfigBrowserEditorKind.Enum => "Enum",
            ConfigBrowserEditorKind.Number => "Number",
            ConfigBrowserEditorKind.Text => "Text",
            ConfigBrowserEditorKind.Path => "Path",
            ConfigBrowserEditorKind.Collection => "Collection",
            ConfigBrowserEditorKind.Group => "Group",
            _ => "Unsupported",
        };
    }

    private enum ConfigBrowserFocus
    {
        Search,
        Tree,
        Detail,
        Editor,
    }

    private enum ConfigBrowserEditMode
    {
        None,
        Text,
        Path,
        Enum,
        Color,
        Collection,
        PromptLayout,
        Group,
    }

    private enum ConfigBrowserConfirmAction
    {
        None,
        Exit,
        ReloadStartup,
        InitializeStartup,
    }

    private sealed record ConfigBrowserListEntry(ConfigBrowserNode Node, int Depth, bool IsExpanded, bool IsDirty)
    {
        public string Label
        {
            get
            {
                var indent = new string(' ', Depth * 2);
                var dirtySuffix = IsDirty ? " *" : string.Empty;

                if (Node.Kind == ConfigBrowserNodeKind.Group)
                {
                    var glyph = IsExpanded ? "▾" : "▸";
                    return $"{indent}{glyph} {Node.DisplayName}{dirtySuffix}";
                }

                return $"{indent}• {Node.DisplayName}{dirtySuffix}";
            }
        }
    }

    private sealed record ConfigDetailEntry(string Text, ConfigDetailEntryKind Kind);

    private enum ConfigDetailEntryKind
    {
        SectionHeading,
        Meta,
        Body,
        Preview,
    }

    private sealed record PromptLayoutEditorItem(string Name, bool Included);

    private sealed record ConfigEditSnapshotEntry(string Path, bool WasStaged, object? Value);

    private sealed record ColorEditorOption(string Label, string? Value);

    private sealed record PathValueDescription(string BaseDirectory, string ResolvedPath, string ExistenceLabel);

}
