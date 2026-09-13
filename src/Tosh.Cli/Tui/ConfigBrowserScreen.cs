using System.Globalization;
using System.Text;
using Tosh.Runtime;
using Tosh.Cli;
using Tosh.Tui.Requests;
using Tosh.Tui;

namespace Tosh.Cli.Tui;

internal sealed partial class ConfigBrowserScreen : ITuiScreen
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
}
