using System.Text;
using Tosh.LanguageServices;
using Tosh.Tui.Editing;

namespace Tosh.Tome;

/// <summary>
/// Language-services overlays: the cursor-anchored completion popup
/// (Ctrl+Space) and the message-line signature help that auto-triggers
/// when the cursor is inside a call site.
/// </summary>
internal sealed partial class TomeApp
{
    // ─── Completion popup state ───────────────────────────────────────────

    private bool _completionOpen;
    private IReadOnlyList<LspCompletionItem> _completionItems = Array.Empty<LspCompletionItem>();
    private int _completionSelected;
    private int _completionScroll;
    private int _completionPrefixLine;
    private int _completionPrefixCol;

    private const int CompletionMaxVisible = 8;
    private const int CompletionMaxWidth = 48;

    private void OpenCompletions()
    {
        if (!IsToshTab()) { _message = "completions: not a .tosh file"; return; }
        if (Environment.GetEnvironmentVariable("TOME_NO_LSP") == "1") { _message = "completions: TOME_NO_LSP=1"; return; }

        var line = _buffer.Cursor.Line;
        var col = _buffer.Cursor.Column;
        var lineText = _buffer.GetLine(line);
        // Walk backwards across word chars to find the prefix start.
        var prefixStart = col;
        while (prefixStart > 0 && IsWordChar(lineText[prefixStart - 1])) prefixStart--;

        var source = string.IsNullOrEmpty(Current.FilePath) ? "untitled.tosh" : Current.FilePath;
        IReadOnlyList<LspCompletionItem> items;
        try
        {
            items = _features.GetCompletionItems(_buffer.GetText(), new LspPosition(line, col), source);
        }
        catch (Exception ex)
        {
            _message = $"completions failed: {ex.Message}";
            return;
        }

        var prefix = lineText.Substring(prefixStart, col - prefixStart);
        var filtered = string.IsNullOrEmpty(prefix)
            ? items
            : items.Where(it => it.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();

        if (filtered.Count == 0) { _message = "no completions"; return; }

        _completionItems = filtered;
        _completionSelected = 0;
        _completionScroll = 0;
        _completionPrefixLine = line;
        _completionPrefixCol = prefixStart;
        _completionOpen = true;
        _message = string.Empty;
    }

    private void CloseCompletions()
    {
        _completionOpen = false;
        _completionItems = Array.Empty<LspCompletionItem>();
        _completionSelected = 0;
        _completionScroll = 0;
    }

    private void AcceptCompletion()
    {
        if (!_completionOpen || _completionSelected < 0 || _completionSelected >= _completionItems.Count)
        {
            CloseCompletions();
            return;
        }

        var item = _completionItems[_completionSelected];
        var insertText = item.InsertText ?? item.Label;

        // Replace the word currently under the prefix start through the end
        // of the contiguous word characters that follow. The user may have
        // typed extra letters since the popup opened — those should be
        // consumed by the chosen completion.
        var lineText = _buffer.GetLine(_completionPrefixLine);
        var endCol = _completionPrefixCol;
        while (endCol < lineText.Length && IsWordChar(lineText[endCol])) endCol++;

        _buffer.ClearSelection();
        _buffer.MoveCursor(new TextLocation(_completionPrefixLine, _completionPrefixCol));
        _buffer.BeginSelection();
        _buffer.MoveCursor(new TextLocation(_completionPrefixLine, endCol));
        _buffer.DeleteSelection();
        _buffer.InsertText(insertText);

        CloseCompletions();
    }

    private bool HandleCompletionKey(ConsoleKeyInfo key)
    {
        // Returns true if the key was consumed by the popup.
        if (!_completionOpen) return false;

        switch (key.Key)
        {
            case ConsoleKey.Escape:
                CloseCompletions();
                return true;
            case ConsoleKey.Enter:
            case ConsoleKey.Tab:
                AcceptCompletion();
                return true;
            case ConsoleKey.UpArrow:
                MoveCompletionSelection(-1);
                return true;
            case ConsoleKey.DownArrow:
                MoveCompletionSelection(+1);
                return true;
            case ConsoleKey.PageUp:
                MoveCompletionSelection(-CompletionMaxVisible);
                return true;
            case ConsoleKey.PageDown:
                MoveCompletionSelection(+CompletionMaxVisible);
                return true;
            // Backspace dismisses if it would walk before the prefix start,
            // otherwise lets the normal handler delete + re-filter.
            case ConsoleKey.Backspace:
                if (_buffer.Cursor.Line == _completionPrefixLine &&
                    _buffer.Cursor.Column <= _completionPrefixCol)
                {
                    CloseCompletions();
                    return true;
                }
                return false;
        }

        // Any printable character: let it through. After the buffer is
        // mutated we re-filter; if no matches remain we dismiss. This is
        // handled by the caller via TryRefilterCompletions().
        return false;
    }

    private void MoveCompletionSelection(int delta)
    {
        if (_completionItems.Count == 0) { CloseCompletions(); return; }
        _completionSelected = Math.Clamp(_completionSelected + delta, 0, _completionItems.Count - 1);
        // Keep the selection visible in the scroll window.
        if (_completionSelected < _completionScroll) _completionScroll = _completionSelected;
        else if (_completionSelected >= _completionScroll + CompletionMaxVisible)
            _completionScroll = _completionSelected - CompletionMaxVisible + 1;
    }

    private void TryRefilterCompletions()
    {
        if (!_completionOpen) return;
        if (_buffer.Cursor.Line != _completionPrefixLine)
        {
            CloseCompletions();
            return;
        }
        if (_buffer.Cursor.Column < _completionPrefixCol)
        {
            CloseCompletions();
            return;
        }
        var lineText = _buffer.GetLine(_completionPrefixLine);
        // Expand prefix to include any chars typed since the popup opened
        // (the cursor might be further right than the original prefix end
        // because the user kept typing word characters).
        var end = _buffer.Cursor.Column;
        // Guard: if a non-word char was typed (space, paren, etc.), dismiss.
        for (var i = _completionPrefixCol; i < end; i++)
        {
            if (!IsWordChar(lineText[i])) { CloseCompletions(); return; }
        }
        var prefix = lineText.Substring(_completionPrefixCol, end - _completionPrefixCol);

        // Re-query — cheap; the engine caches its parse state internally.
        var source = string.IsNullOrEmpty(Current.FilePath) ? "untitled.tosh" : Current.FilePath;
        IReadOnlyList<LspCompletionItem> items;
        try
        {
            items = _features.GetCompletionItems(_buffer.GetText(), new LspPosition(_buffer.Cursor.Line, end), source);
        }
        catch
        {
            CloseCompletions();
            return;
        }

        var filtered = string.IsNullOrEmpty(prefix)
            ? items
            : items.Where(it => it.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();

        if (filtered.Count == 0) { CloseCompletions(); return; }
        _completionItems = filtered;
        _completionSelected = Math.Min(_completionSelected, filtered.Count - 1);
        if (_completionSelected < 0) _completionSelected = 0;
        if (_completionScroll > _completionSelected) _completionScroll = _completionSelected;
    }

    // ─── Signature help state ─────────────────────────────────────────────

    private string _signatureHelpText = string.Empty;
    private string _signatureHelpForText = string.Empty;
    private TextLocation _signatureHelpForCursor;

    private void RefreshSignatureHelp()
    {
        if (!IsToshTab()) { _signatureHelpText = string.Empty; return; }
        if (Environment.GetEnvironmentVariable("TOME_NO_LSP") == "1") { _signatureHelpText = string.Empty; return; }

        var text = _buffer.GetText();
        var cursor = _buffer.Cursor;
        // Cheap cache: same text + same cursor position ⇒ no re-query.
        if (string.Equals(text, _signatureHelpForText, StringComparison.Ordinal) &&
            _signatureHelpForCursor == cursor)
            return;
        _signatureHelpForText = text;
        _signatureHelpForCursor = cursor;

        var source = string.IsNullOrEmpty(Current.FilePath) ? "untitled.tosh" : Current.FilePath;
        LspSignatureHelp? help;
        try { help = _features.GetSignatureHelp(text, source, new LspPosition(cursor.Line, cursor.Column)); }
        catch { _signatureHelpText = string.Empty; return; }

        if (help is null || help.Signatures.Count == 0) { _signatureHelpText = string.Empty; return; }
        var sig = help.Signatures[Math.Clamp(help.ActiveSignature, 0, help.Signatures.Count - 1)];
        var active = help.ActiveParameter;
        _signatureHelpText = FormatSignature(sig, active);
    }

    private static string FormatSignature(LspSignatureInformation sig, int activeParam)
    {
        var ps = sig.Parameters;
        if (ps is null || ps.Count == 0) return sig.Label;
        // Highlight the active parameter via inverse video; rebuild the
        // signature label string from parameter labels so we don't rely on
        // the engine giving us byte offsets.
        var sb = new StringBuilder();
        // Try to preserve everything before the first parameter occurrence
        // by finding it in the label; if not found, use the parameter list
        // alone.
        var openParen = sig.Label.IndexOf('(');
        var closeParen = sig.Label.LastIndexOf(')');
        if (openParen >= 0)
        {
            sb.Append(sig.Label, 0, openParen + 1);
        }

        for (var i = 0; i < ps.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            if (i == activeParam) sb.Append("\u001b[7m");
            sb.Append(ps[i].Label);
            if (i == activeParam) sb.Append("\u001b[27m");
        }

        if (openParen >= 0 && closeParen > openParen)
            sb.Append(sig.Label, closeParen, sig.Label.Length - closeParen);
        else
            sb.Append(')');

        return sb.ToString();
    }

    // ─── Painting the completion popup ────────────────────────────────────

    private void PaintCompletionPopup(StringBuilder sb, int gutterWidth, int editorTopRow, int editorHeight, int screenWidth)
    {
        if (!_completionOpen || _completionItems.Count == 0) return;

        var visible = Math.Min(CompletionMaxVisible, _completionItems.Count);
        var (cursorRow, cursorCol) = _view.GetCursorScreenPosition();
        var anchorRow = editorTopRow + cursorRow;   // 1-based terminal row of cursor
        var anchorCol = gutterWidth + cursorCol + 1; // 1-based terminal col of cursor

        // Decide vertical placement: below cursor by default; flip above when
        // there isn't enough room beneath.
        var spaceBelow = (editorTopRow + editorHeight - 1) - anchorRow;
        var openDown = spaceBelow >= visible + 1;
        var startRow = openDown ? anchorRow + 1 : anchorRow - visible;
        if (startRow < editorTopRow) startRow = editorTopRow;

        // Compute popup width from items + clamp.
        var width = 0;
        for (var i = 0; i < _completionItems.Count; i++)
        {
            var w = _completionItems[i].Label.Length + 4; // icon + padding
            if (_completionItems[i].Detail is { Length: > 0 } d)
                w += Math.Min(20, d.Length) + 2;
            if (w > width) width = w;
        }
        width = Math.Min(CompletionMaxWidth, Math.Max(16, width));
        if (anchorCol + width > screenWidth) anchorCol = Math.Max(1, screenWidth - width);

        for (var row = 0; row < visible; row++)
        {
            var idx = _completionScroll + row;
            if (idx >= _completionItems.Count) break;
            var item = _completionItems[idx];
            sb.Append("\u001b[").Append(startRow + row).Append(';').Append(anchorCol).Append('H');

            var selected = idx == _completionSelected;
            // Frame: reverse for selected, dim background otherwise.
            sb.Append(selected ? "\u001b[7m" : "\u001b[48;5;236m");

            var icon = KindIcon(item.Kind);
            var detail = item.Detail ?? string.Empty;
            var labelMax = Math.Max(4, width - 4 - (detail.Length > 0 ? Math.Min(20, detail.Length) + 2 : 0));
            var label = TruncateTo(item.Label, labelMax);
            var line = new StringBuilder();
            line.Append(' ').Append(icon).Append(' ').Append(label);
            if (detail.Length > 0)
            {
                line.Append("  ");
                line.Append(TruncateTo(detail, Math.Min(20, detail.Length)));
            }
            var padded = line.ToString();
            if (padded.Length < width) padded += new string(' ', width - padded.Length);
            else padded = padded[..width];
            sb.Append(padded);
            sb.Append("\u001b[0m");
        }
    }

    private static string TruncateTo(string s, int max)
    {
        if (s.Length <= max) return s;
        if (max <= 1) return s[..max];
        return s[..(max - 1)] + "…";
    }

    private static string KindIcon(int kind) => kind switch
    {
        // LSP CompletionItemKind values.
        2 => "ƒ",   // Method
        3 => "ƒ",   // Function
        4 => "ƒ",   // Constructor
        5 => "·",   // Field
        6 => "v",   // Variable
        7 => "C",   // Class
        8 => "I",   // Interface
        9 => "M",   // Module
        10 => "·",  // Property
        14 => "k",  // Keyword
        21 => "#",  // Constant
        22 => "T",  // Struct
        25 => "T",  // TypeParameter
        _ => "•",
    };
}
