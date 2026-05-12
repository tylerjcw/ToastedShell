using System.Collections.Concurrent;
using System.Text;
using Tosh.Tome.Workspace;

namespace Tosh.Tome;

/// <summary>
/// Left-dock file/folder tree shown when a workspace is active. Owns its
/// own selection, scroll, and expand/collapse state. Rendering is one
/// fully-formed visible-line string per terminal row; <see cref="TomeApp"/>
/// composites these into the per-frame buffer alongside the gutter and
/// editor columns.
/// </summary>
/// <remarks>
/// Directory children are read on demand the first time a node is
/// expanded, then cached. A per-root <see cref="FileSystemWatcher"/>
/// queues changes; <see cref="ConsumeChanges"/> drains the queue on the
/// UI thread and invalidates the affected cached directories so the
/// next render reflects fresh contents.
/// </remarks>
internal sealed class ExplorerPane : IDisposable
{
    private readonly List<Node> _roots = new();
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private List<Node> _flat = new();
    private int _selected;
    private int _scroll;

    private string[] _excludeSegments = Array.Empty<string>();

    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly ConcurrentQueue<string> _pendingChanges = new();

    public bool Open { get; set; } = true;
    public int Width { get; set; } = 32;

    /// <summary>Path of the currently selected node, or null if the pane is empty.</summary>
    public string? SelectedPath => _selected >= 0 && _selected < _flat.Count ? _flat[_selected].Path : null;
    public bool SelectedIsDirectory => _selected >= 0 && _selected < _flat.Count && _flat[_selected].IsDirectory;

    public void LoadFromWorkspace(Workspace.Workspace workspace)
    {
        DisposeWatchers();
        _roots.Clear();
        _expanded.Clear();
        _excludeSegments = workspace.Exclude.ToArray();
        foreach (var folder in workspace.Folders)
        {
            var label = !string.IsNullOrEmpty(folder.Alias) ? folder.Alias : Path.GetFileName(folder.Path);
            if (string.IsNullOrEmpty(label)) label = folder.Path;
            var root = new Node(folder.Path, label, isDirectory: true, depth: 0);
            _roots.Add(root);
            _expanded.Add(folder.Path); // roots start expanded
            AttachWatcher(folder.Path);
        }
        Width = workspace.Layout.ExplorerWidth;
        Open = workspace.Layout.ExplorerOpen;
        _selected = 0;
        _scroll = 0;
        RebuildFlatten();
    }

    public void Clear()
    {
        DisposeWatchers();
        _roots.Clear();
        _expanded.Clear();
        _flat.Clear();
        _selected = 0;
        _scroll = 0;
    }

    public bool HasRoots => _roots.Count > 0;

    // ─── Navigation ───────────────────────────────────────────────────

    public void MoveDown() { if (_selected < _flat.Count - 1) _selected++; }
    public void MoveUp() { if (_selected > 0) _selected--; }
    public void PageDown(int visibleRows) { _selected = Math.Min(_flat.Count - 1, _selected + Math.Max(1, visibleRows - 1)); }
    public void PageUp(int visibleRows) { _selected = Math.Max(0, _selected - Math.Max(1, visibleRows - 1)); }
    public void MoveHome() => _selected = 0;
    public void MoveEnd() => _selected = Math.Max(0, _flat.Count - 1);

    /// <summary>Toggle expand/collapse on the selected node. Returns true if the node is a directory.</summary>
    public bool ToggleSelected()
    {
        if (_selected < 0 || _selected >= _flat.Count) return false;
        var node = _flat[_selected];
        if (!node.IsDirectory) return false;
        if (_expanded.Contains(node.Path)) _expanded.Remove(node.Path);
        else _expanded.Add(node.Path);
        RebuildFlatten();
        return true;
    }

    /// <summary>Expand the selected dir if it's collapsed; else move into the first child.</summary>
    public void ExpandOrEnter()
    {
        if (_selected < 0 || _selected >= _flat.Count) return;
        var node = _flat[_selected];
        if (!node.IsDirectory) return;
        if (!_expanded.Contains(node.Path)) { _expanded.Add(node.Path); RebuildFlatten(); return; }
        // already expanded — drop into the first child if there is one
        if (_selected + 1 < _flat.Count && _flat[_selected + 1].Depth > node.Depth) _selected++;
    }

    /// <summary>Collapse the selected dir if it's expanded; else jump to its parent.</summary>
    public void CollapseOrParent()
    {
        if (_selected < 0 || _selected >= _flat.Count) return;
        var node = _flat[_selected];
        if (node.IsDirectory && _expanded.Contains(node.Path))
        {
            _expanded.Remove(node.Path);
            RebuildFlatten();
            return;
        }
        // jump to parent — first earlier entry with smaller depth
        var i = _selected - 1;
        while (i >= 0 && _flat[i].Depth >= node.Depth) i--;
        if (i >= 0) _selected = i;
    }

    // ─── Rendering ────────────────────────────────────────────────────

    /// <summary>
    /// Returns one fully-formed pane line (ANSI-styled, exactly
    /// <see cref="Width"/> visible columns wide) for the given visible row,
    /// or a blank-filled string when the row is past the end of the tree.
    /// </summary>
    public string RenderRow(int visibleRow, int visibleRows, bool focused)
    {
        EnsureSelectionVisible(visibleRows);

        var index = _scroll + visibleRow;
        if (index < 0 || index >= _flat.Count) return new string(' ', Width);

        var node = _flat[index];
        var selected = index == _selected;
        var indent = node.Depth * 2;
        var chevron = node.IsDirectory ? (_expanded.Contains(node.Path) ? "▾ " : "▸ ") : "  ";
        var icon = node.IsDirectory ? "📁 " : "  ";
        var raw = new string(' ', indent) + chevron + icon + node.Label;
        var visible = TruncateToWidth(raw, Width);

        var sb = new StringBuilder(visible.Length + 32);
        if (selected)
        {
            if (focused) sb.Append("\u001b[7m");          // reverse
            else sb.Append("\u001b[48;5;238m");   // dim selection bg when unfocused
        }
        else if (node.IsDirectory)
        {
            sb.Append("\u001b[1m"); // bold for directories
        }
        sb.Append(visible);
        if (visible.Length < Width) sb.Append(new string(' ', Width - visible.Length));
        sb.Append("\u001b[0m");
        return sb.ToString();
    }

    private void EnsureSelectionVisible(int visibleRows)
    {
        if (visibleRows <= 0) return;
        if (_selected < _scroll) _scroll = _selected;
        else if (_selected >= _scroll + visibleRows) _scroll = _selected - visibleRows + 1;
        _scroll = Math.Max(0, Math.Min(_scroll, Math.Max(0, _flat.Count - visibleRows)));
    }

    private static string TruncateToWidth(string s, int width)
    {
        if (s.Length <= width) return s;
        if (width <= 1) return s.Substring(0, width);
        return s.Substring(0, width - 1) + "…";
    }

    // ─── Flatten + lazy child discovery ──────────────────────────────

    private void RebuildFlatten()
    {
        _flat = new List<Node>();
        foreach (var root in _roots) AppendVisible(root);
        if (_selected >= _flat.Count) _selected = Math.Max(0, _flat.Count - 1);
    }

    private void AppendVisible(Node node)
    {
        _flat.Add(node);
        if (!node.IsDirectory) return;
        if (!_expanded.Contains(node.Path)) return;

        EnsureChildrenLoaded(node);
        foreach (var child in node.Children) AppendVisible(child);
    }

    private void EnsureChildrenLoaded(Node node)
    {
        if (node.ChildrenLoaded) return;
        node.ChildrenLoaded = true;
        if (!Directory.Exists(node.Path)) return;

        IEnumerable<string> dirs;
        IEnumerable<string> files;
        try
        {
            dirs = Directory.EnumerateDirectories(node.Path);
            files = Directory.EnumerateFiles(node.Path);
        }
        catch
        {
            return; // permission errors etc. — render as empty
        }

        foreach (var d in dirs.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(d);
            if (ShouldExclude(name)) continue;
            node.Children.Add(new Node(d, name, isDirectory: true, depth: node.Depth + 1));
        }
        foreach (var f in files.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(f);
            if (ShouldExclude(name)) continue;
            node.Children.Add(new Node(f, name, isDirectory: false, depth: node.Depth + 1));
        }
    }

    private bool ShouldExclude(string segment)
    {
        foreach (var pat in _excludeSegments)
        {
            if (string.Equals(pat, segment, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    // ─── FileSystemWatcher integration ───────────────────────────────

    /// <summary>
    /// Drains queued filesystem events and invalidates any cached
    /// directories that contain a changed path. Returns true if anything
    /// was invalidated (i.e. the caller should redraw).
    /// </summary>
    public bool ConsumeChanges()
    {
        if (_pendingChanges.IsEmpty) return false;
        var affected = new HashSet<string>(StringComparer.Ordinal);
        while (_pendingChanges.TryDequeue(out var path))
        {
            // The parent directory is what needs reloading.
            var parent = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent)) affected.Add(parent);
        }
        var dirty = false;
        foreach (var root in _roots) dirty |= InvalidateMatching(root, affected);
        if (dirty) RebuildFlatten();
        return dirty;
    }

    private static bool InvalidateMatching(Node node, HashSet<string> affected)
    {
        if (!node.IsDirectory) return false;
        var dirty = false;
        if (node.ChildrenLoaded && affected.Contains(node.Path))
        {
            node.Children.Clear();
            node.ChildrenLoaded = false;
            dirty = true;
        }
        foreach (var child in node.Children) dirty |= InvalidateMatching(child, affected);
        return dirty;
    }

    private void AttachWatcher(string path)
    {
        if (!Directory.Exists(path)) return;
        FileSystemWatcher w;
        try
        {
            w = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
            };
        }
        catch
        {
            return; // platform unsupported or permission denied
        }
        w.Created += OnFsEvent;
        w.Deleted += OnFsEvent;
        w.Renamed += OnFsRenamed;
        try { w.EnableRaisingEvents = true; }
        catch { w.Dispose(); return; }
        _watchers.Add(w);
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e) => _pendingChanges.Enqueue(e.FullPath);
    private void OnFsRenamed(object sender, RenamedEventArgs e)
    {
        _pendingChanges.Enqueue(e.OldFullPath);
        _pendingChanges.Enqueue(e.FullPath);
    }

    private void DisposeWatchers()
    {
        foreach (var w in _watchers)
        {
            try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
        }
        _watchers.Clear();
        while (_pendingChanges.TryDequeue(out _)) { }
    }

    public void Dispose() => DisposeWatchers();

    private sealed class Node
    {
        public string Path { get; }
        public string Label { get; }
        public bool IsDirectory { get; }
        public int Depth { get; }
        public List<Node> Children { get; } = new();
        public bool ChildrenLoaded { get; set; }

        public Node(string path, string label, bool isDirectory, int depth)
        {
            Path = path;
            Label = label;
            IsDirectory = isDirectory;
            Depth = depth;
        }
    }
}
