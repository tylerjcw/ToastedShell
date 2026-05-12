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

    public Tab(string filePath, string initialText, ISyntaxColorizer? colorizer)
    {
        Buffer = new TextBuffer(initialText);
        View = new TextEditorView(Buffer);
        FilePath = filePath;
        Colorizer = colorizer;
    }

    public string DisplayName =>
        string.IsNullOrEmpty(FilePath) ? "[no name]" : Path.GetFileName(FilePath);
}
