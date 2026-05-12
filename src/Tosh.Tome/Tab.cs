using Tosh.LanguageServices;
using Tosh.Tui.Editing;

namespace Tosh.Tome;

/// <summary>
/// One open document inside Tōme. Each tab carries its own buffer, view,
/// file path, syntax colorizer, and last-search state so the editor can host
/// many files at once with independent cursor/scroll/history.
/// </summary>
internal sealed class Tab
{
    public TextBuffer Buffer { get; }
    public TextEditorView View { get; }
    public string FilePath { get; set; }
    public ISyntaxColorizer? Colorizer { get; set; }
    public string LastSearch { get; set; } = string.Empty;
    public bool SearchRegex { get; set; }
    public bool SearchIgnoreCase { get; set; }

    // Diagnostics cache. Recomputed only when the buffer text changes.
    public IReadOnlyList<LspDiagnostic> Diagnostics { get; set; } = Array.Empty<LspDiagnostic>();
    public string DiagnosticsForText { get; set; } = string.Empty;
    public bool DiagnosticsPopulated { get; set; }

    // Disk-stamps for reload-on-change detection. Set on load/save; the
    // render loop polls the file each frame (rate-limited) and surfaces
    // a reload prompt when these no longer match what's on disk.
    public DateTime DiskMTimeUtc { get; set; }
    public long DiskSize { get; set; } = -1;
    public bool ExternalChangePending { get; set; }

    /// <summary>Set of buffer line indices flagged with breakpoints.</summary>
    public HashSet<int> Breakpoints { get; } = new();

    /// <summary>
    /// Lazy git diff tracker. Null when the tab has no file path (a new
    /// unsaved buffer). Created on first access via <see cref="EnsureGitDiff"/>.
    /// </summary>
    public GitDiffTracker? GitDiff { get; private set; }

    public void EnsureGitDiff()
    {
        if (GitDiff is null && !string.IsNullOrEmpty(FilePath))
            GitDiff = new GitDiffTracker(FilePath);
    }

    public Tab(string filePath, string initialText, ISyntaxColorizer? colorizer)
    {
        Buffer = new TextBuffer(initialText);
        View = new TextEditorView(Buffer);
        FilePath = filePath;
        Colorizer = colorizer;

        if (!string.IsNullOrEmpty(filePath))
        {
            PersistentUndoStore.TryRestore(filePath, Buffer);
            StampFromDisk();
        }
    }

    public void StampFromDisk()
    {
        if (string.IsNullOrEmpty(FilePath)) { DiskSize = -1; return; }
        try
        {
            var fi = new FileInfo(FilePath);
            if (fi.Exists)
            {
                DiskMTimeUtc = fi.LastWriteTimeUtc;
                DiskSize = fi.Length;
            }
            else
            {
                DiskMTimeUtc = default;
                DiskSize = -1;
            }
        }
        catch
        {
            DiskMTimeUtc = default;
            DiskSize = -1;
        }
        ExternalChangePending = false;
    }

    public string DisplayName =>
        string.IsNullOrEmpty(FilePath) ? "[no name]" : Path.GetFileName(FilePath);
}
