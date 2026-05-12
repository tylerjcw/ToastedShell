using Tosh.LanguageServices;
using Tosh.Tui.Editing;

namespace Tosh.Tome;

/// <summary>
/// Semantic-tokens-driven colorizer for <c>.tosh</c> sources. Backed by
/// <see cref="ToshLanguageFeatures.GetSemanticTokens"/> — the same engine
/// that powers the TōSh LSP server — so highlighting reflects the full
/// parsed AST (commands vs. functions vs. classes, etc.) rather than the
/// per-line lexical heuristics used by the legacy colorizer.
///
/// Tokens are computed lazily per document text on first access and cached
/// until the buffer text changes. Recompute is O(file size) and happens
/// on the render thread, which is acceptable for documents up to a few
/// thousand lines.
/// </summary>
internal sealed class LspBackedColorizer : ISyntaxColorizer
{
    private readonly ToshLanguageFeatures _features;
    private readonly string _sourceName;
    private readonly Func<string> _readText;

    private string? _lastText;
    private List<List<StyledSpan>> _spansByLine = new();

    // ANSI 256-color foregrounds, indexed by LSP semantic-token-type id.
    // The legend (see ToshLanguageFeatures.SemanticTokenTypes) is:
    //   0 comment, 1 keyword, 2 string, 3 number,
    //   4 variable, 5 function, 6 type, 7 operator
    private static readonly string[] StyleByType =
    [
        "\u001b[38;5;244m",  // comment   — dim grey
        "\u001b[38;5;141m",  // keyword   — soft purple
        "\u001b[38;5;150m",  // string    — green
        "\u001b[38;5;215m",  // number    — orange
        "\u001b[38;5;110m",  // variable  — soft blue
        "\u001b[38;5;180m",  // function  — tan
        "\u001b[38;5;180m",  // type      — tan
        "\u001b[38;5;110m",  // operator  — soft blue
    ];

    // Italic-faint for documentation comments (## …). Composes with the
    // comment color.
    private const string DocCommentDecor = "\u001b[3m";

    public LspBackedColorizer(ToshLanguageFeatures features, string sourceName, Func<string> readText)
    {
        _features = features;
        _sourceName = sourceName;
        _readText = readText;
    }

    public IReadOnlyList<StyledSpan> Colorize(string line, int lineIndex)
    {
        var text = _readText();
        if (!ReferenceEquals(text, _lastText) && text != _lastText)
        {
            Rebuild(text);
            _lastText = text;
        }
        if (lineIndex < 0 || lineIndex >= _spansByLine.Count)
            return Array.Empty<StyledSpan>();
        return _spansByLine[lineIndex];
    }

    private void Rebuild(string text)
    {
        var lineCount = CountLines(text);
        _spansByLine = new List<List<StyledSpan>>(lineCount);
        for (var i = 0; i < lineCount; i++) _spansByLine.Add(new List<StyledSpan>());

        LspSemanticTokens tokens;
        try
        {
            tokens = _features.GetSemanticTokens(text, _sourceName);
        }
        catch
        {
            // Parse/lex failures fall through to no highlighting rather than
            // taking down the editor.
            return;
        }

        // LSP semantic-tokens encoding: groups of 5 ints, delta-line then
        // delta-start within line, then length, type, modifiers.
        var data = tokens.Data;
        var currentLine = 0;
        var currentChar = 0;
        for (var i = 0; i + 4 < data.Count; i += 5)
        {
            var deltaLine = data[i];
            var deltaChar = data[i + 1];
            var length = data[i + 2];
            var type = data[i + 3];
            var modifiers = data[i + 4];

            if (deltaLine != 0)
            {
                currentLine += deltaLine;
                currentChar = deltaChar;
            }
            else
            {
                currentChar += deltaChar;
            }
            if (length <= 0 || type < 0 || type >= StyleByType.Length) continue;
            if (currentLine < 0 || currentLine >= _spansByLine.Count) continue;

            var ansi = StyleByType[type];
            // Documentation modifier (bit 2) on a comment → add italics.
            if (type == 0 && (modifiers & 0x04) != 0) ansi = DocCommentDecor + ansi;
            _spansByLine[currentLine].Add(new StyledSpan(currentChar, length, ansi));
        }

        foreach (var list in _spansByLine)
            list.Sort(static (a, b) => a.Start.CompareTo(b.Start));
    }

    private static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return 1;
        var count = 1;
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n') count++;
        return count;
    }
}
