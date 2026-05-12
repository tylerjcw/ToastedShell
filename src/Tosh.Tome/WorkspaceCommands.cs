using Tosh.Tome.Workspace;

namespace Tosh.Tome;

/// <summary>
/// Workspace-related verbs reachable via <c>:workspace</c> (alias <c>:ws</c>).
/// </summary>
internal sealed partial class TomeApp
{
    private void HandleWorkspaceVerb(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            ShowWorkspaceStatus();
            return;
        }

        var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var sub = parts[0];
        var rest = parts.Length > 1 ? parts[1].Trim() : string.Empty;

        switch (sub)
        {
            case "open":
            case "load":
                WorkspaceOpen(rest);
                return;
            case "save":
                WorkspaceSave(rest);
                return;
            case "close":
                WorkspaceClose();
                return;
            case "info":
            case "status":
                ShowWorkspaceStatus();
                return;
            case "new":
                WorkspaceNew(rest);
                return;
            case "add":
                WorkspaceAddFolder(rest);
                return;
            default:
                _message = $"workspace: unknown subcommand '{sub}' (open|save|close|info|new|add)";
                return;
        }
    }

    private void WorkspaceOpen(string path)
    {
        if (string.IsNullOrEmpty(path)) { _message = "workspace open: path required"; return; }
        var resolved = Path.GetFullPath(path);
        if (!File.Exists(resolved)) { _message = $"workspace open: '{resolved}' not found"; return; }

        Workspace.Workspace ws;
        try { ws = WorkspaceFile.Load(resolved); }
        catch (WorkspaceParseException ex) { _message = $"workspace parse error: {ex.Message}"; return; }
        catch (Exception ex) { _message = $"workspace open failed: {ex.Message}"; return; }

        _workspace = ws;
        _explorer.LoadFromWorkspace(ws);
        var restored = RestoreWorkspaceTabs(ws);
        _message = restored > 0
            ? $"workspace '{ws.Name}' loaded ({ws.Folders.Count} folder(s), {restored} tab(s) restored)"
            : $"workspace '{ws.Name}' loaded ({ws.Folders.Count} folder(s))";
    }

    /// <summary>
    /// Loads a <c>.tome</c> workspace at startup, before <c>Run()</c>
    /// begins the render loop. If any tabs are restored, drops the
    /// initial placeholder buffer the constructor created so the user
    /// lands directly on a restored file.
    /// </summary>
    public void OpenWorkspaceAtStartup(string path)
    {
        var hadEmptyPlaceholder = _tabs.Count == 1
            && string.IsNullOrEmpty(_tabs[0].FilePath)
            && !_tabs[0].Buffer.IsModified;
        var tabCountBefore = _tabs.Count;
        WorkspaceOpen(path);
        if (hadEmptyPlaceholder && _tabs.Count > tabCountBefore)
        {
            _tabs.RemoveAt(0);
            _active = Math.Max(0, _active - 1);
        }
    }

    /// <summary>
    /// Opens <paramref name="directory"/> as an ad-hoc single-folder
    /// workspace with no source <c>.tome</c> file. The explorer pane
    /// lights up immediately; <c>:workspace save &lt;path&gt;</c>
    /// persists it.
    /// </summary>
    public void OpenDirectoryAsWorkspace(string directory)
    {
        var resolved = Path.GetFullPath(directory);
        if (!Directory.Exists(resolved)) { _message = $"workspace: '{resolved}' is not a directory"; return; }
        var name = Path.GetFileName(resolved.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(name)) name = resolved;
        var ws = new Workspace.Workspace
        {
            Name = name,
            Folders = new[] { new Workspace.WorkspaceFolder(resolved) },
        };
        _workspace = ws;
        _explorer.LoadFromWorkspace(ws);
        _message = $"workspace '{name}' (unsaved, {resolved})";
    }

    private void WorkspaceSave(string path)
    {
        if (_workspace is null) { _message = "workspace save: no workspace loaded — use ':workspace new <path>' first"; return; }

        var target = string.IsNullOrEmpty(path) ? _workspace.SourcePath : Path.GetFullPath(path);
        if (string.IsNullOrEmpty(target)) { _message = "workspace save: path required (no source path on this workspace)"; return; }

        // Snapshot the currently open tab paths so reopening the workspace
        // restores the same view.
        var openFiles = _tabs
            .Where(t => !string.IsNullOrEmpty(t.FilePath))
            .Select(t => t.FilePath)
            .ToArray();

        var updated = _workspace with { OpenFiles = openFiles, SourcePath = target };
        try { WorkspaceFile.Save(updated, target); }
        catch (Exception ex) { _message = $"workspace save failed: {ex.Message}"; return; }
        _workspace = updated;
        _message = $"workspace saved to {target}";
    }

    private void WorkspaceClose()
    {
        if (_workspace is null) { _message = "workspace close: no workspace loaded"; return; }
        var name = _workspace.Name;
        _workspace = null;
        _explorer.Clear();
        _focusExplorer = false;
        _message = $"workspace '{name}' closed";
    }

    private void WorkspaceNew(string path)
    {
        if (string.IsNullOrEmpty(path)) { _message = "workspace new: path required"; return; }
        var resolved = Path.GetFullPath(path);
        var name = Path.GetFileNameWithoutExtension(resolved);
        if (string.IsNullOrEmpty(name)) name = "workspace";
        _workspace = new Workspace.Workspace
        {
            Name = name,
            SourcePath = resolved,
        };
        _message = $"new workspace '{name}' (unsaved) — use ':ws add <folder>' then ':ws save'";
    }

    private void WorkspaceAddFolder(string spec)
    {
        if (_workspace is null) { _message = "workspace add: no workspace loaded"; return; }
        if (string.IsNullOrEmpty(spec)) { _message = "workspace add: folder path required"; return; }

        // Optional "as alias" suffix.
        string folderPath = spec;
        string? alias = null;
        var idx = spec.LastIndexOf(" as ", StringComparison.Ordinal);
        if (idx > 0)
        {
            folderPath = spec[..idx].Trim();
            alias = spec[(idx + 4)..].Trim().Trim('"', '\'');
        }
        var resolved = Path.GetFullPath(folderPath);
        if (!Directory.Exists(resolved)) { _message = $"workspace add: directory '{resolved}' does not exist"; return; }

        var folders = _workspace.Folders.ToList();
        folders.Add(new WorkspaceFolder(resolved, alias));
        _workspace = _workspace with { Folders = folders };
        _message = $"added folder '{resolved}'" + (alias is not null ? $" as '{alias}'" : "");
    }

    private void ShowWorkspaceStatus()
    {
        if (_workspace is null) { _message = "no workspace loaded"; return; }
        var w = _workspace;
        _message = $"workspace '{w.Name}': {w.Folders.Count} folder(s), {w.OpenFiles.Count} restored file(s)"
            + (w.SourcePath is not null ? $"  [{w.SourcePath}]" : " [unsaved]");
    }

    /// <summary>
    /// Opens each file listed under <c>open [...]</c> in the workspace as
    /// a fresh tab. Missing files are skipped silently rather than
    /// aborting the load. Returns the number of tabs added.
    /// </summary>
    private int RestoreWorkspaceTabs(Workspace.Workspace ws)
    {
        var added = 0;
        foreach (var rel in ws.OpenFiles)
        {
            var path = Path.IsPathRooted(rel)
                ? rel
                : Path.GetFullPath(rel, Path.GetDirectoryName(ws.SourcePath ?? Environment.CurrentDirectory) ?? Environment.CurrentDirectory);
            if (!File.Exists(path)) continue;
            string text;
            try { text = File.ReadAllText(path); }
            catch { continue; }
            var tab = new Tab(path, text, null);
            tab.Colorizer = ResolveColorizer(tab);
            _tabs.Add(tab);
            added++;
        }
        if (added > 0) _active = _tabs.Count - 1;
        return added;
    }

    // ─── Explorer key routing ────────────────────────────────────────

    private void HandleExplorerKey(ConsoleKeyInfo key)
    {
        var visibleRows = Math.Max(1, _terminal.Height - StatusLineHeight - MessageLineHeight - TabBarHeight);

        switch (key.Key)
        {
            case ConsoleKey.Escape:
            case ConsoleKey.Tab:
                _focusExplorer = false;
                _message = string.Empty;
                return;
            case ConsoleKey.UpArrow:
            case ConsoleKey.K:
                _explorer.MoveUp();
                return;
            case ConsoleKey.DownArrow:
            case ConsoleKey.J:
                _explorer.MoveDown();
                return;
            case ConsoleKey.RightArrow:
            case ConsoleKey.L:
                _explorer.ExpandOrEnter();
                return;
            case ConsoleKey.LeftArrow:
            case ConsoleKey.H:
                _explorer.CollapseOrParent();
                return;
            case ConsoleKey.Enter:
                OpenSelectedExplorerEntry();
                return;
            case ConsoleKey.Spacebar:
                _explorer.ToggleSelected();
                return;
            case ConsoleKey.PageDown:
                _explorer.PageDown(visibleRows);
                return;
            case ConsoleKey.PageUp:
                _explorer.PageUp(visibleRows);
                return;
            case ConsoleKey.Home:
                _explorer.MoveHome();
                return;
            case ConsoleKey.End:
                _explorer.MoveEnd();
                return;
        }
    }

    private void OpenSelectedExplorerEntry()
    {
        var path = _explorer.SelectedPath;
        if (path is null) return;
        if (_explorer.SelectedIsDirectory)
        {
            _explorer.ToggleSelected();
            return;
        }
        // Avoid duplicate tabs: if the file is already open, switch to it.
        for (var i = 0; i < _tabs.Count; i++)
        {
            if (string.Equals(_tabs[i].FilePath, path, StringComparison.Ordinal))
            {
                _active = i;
                _focusExplorer = false;
                _message = $"switched to {Path.GetFileName(path)}";
                return;
            }
        }
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) { _message = $"open failed: {ex.Message}"; return; }
        var tab = new Tab(path, text, null);
        tab.Colorizer = ResolveColorizer(tab);
        _tabs.Add(tab);
        _active = _tabs.Count - 1;
        _focusExplorer = false;
        _message = $"opened {Path.GetFileName(path)}";
    }
}
