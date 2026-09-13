using System.Globalization;
using System.Text;
using Tosh.Runtime;
using Tosh.Cli;
using Tosh.Tui.Requests;
using Tosh.Tui;

namespace Tosh.Cli.Tui;

/// <summary>
/// The config browser's value editors: text, enum, colour, path, collection and
/// prompt-layout editing, and the staging they write into.
/// </summary>
/// <remarks>
/// Split out of <c>ConfigBrowserScreen.cs</c>. This is the half that mutates. Staging is
/// in memory; only <c>ApplyStagedChanges</c> and <c>SaveConfiguration</c> — both in the
/// core file — reach the runtime and the config file.
/// </remarks>
internal sealed partial class ConfigBrowserScreen
{
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

    private ConfigBrowserNode? GetEditingNode()
    {
        return _editingPath is not null && _schema.NodesByPath.TryGetValue(_editingPath, out var node)
            ? node
            : null;
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
}
