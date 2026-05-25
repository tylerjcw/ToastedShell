using System.Text;
using Tosh.LanguageServices;
using Tosh.Tome.Theme;
using Tosh.Tui.Editing;

namespace Tosh.Tome;

/// <summary>
/// LSP code-action popup (Alt+.). Collects diagnostics on the cursor line,
/// queries <see cref="ToshLanguageFeatures.GetCodeActions"/>, presents a
/// menu, and applies the chosen <see cref="LspWorkspaceEdit"/> to the buffer.
/// </summary>
internal sealed partial class TomeApp
{
    // ─── State ───────────────────────────────────────────────────────────

    private bool _codeActionsOpen;
    private IReadOnlyList<LspCodeAction> _codeActionItems = Array.Empty<LspCodeAction>();
    private int _codeActionsSelected;
    private const int CodeActionsMaxVisible = 8;

    // ─── Open / close ─────────────────────────────────────────────────────

    private void OpenCodeActions()
    {
        if (!IsToshTab()) { _message = "code actions: not a .tosh file"; return; }
        if (Environment.GetEnvironmentVariable("TOME_NO_LSP") == "1") { _message = "code actions: TOME_NO_LSP=1"; return; }

        var text = _buffer.GetText();
        var source = string.IsNullOrEmpty(Current.FilePath) ? "untitled.tosh" : Current.FilePath;
        var cursorLine = _buffer.Cursor.Line;

        var lineDiags = (Current.Diagnostics ?? Array.Empty<LspDiagnostic>())
            .Where(d => d.Range.Start.Line <= cursorLine && d.Range.End.Line >= cursorLine)
            .ToArray();

        IReadOnlyList<LspCodeAction> actions;
        try
        {
            actions = _features.GetCodeActions(text, source, new LspCodeActionContext(lineDiags));
        }
        catch (Exception ex)
        {
            _message = $"code actions: {ex.Message}";
            return;
        }

        if (actions.Count == 0) { _message = "no code actions"; return; }

        _codeActionItems = actions;
        _codeActionsSelected = 0;
        _codeActionsOpen = true;
        _message = string.Empty;
    }

    private void CloseCodeActions()
    {
        _codeActionsOpen = false;
        _codeActionItems = Array.Empty<LspCodeAction>();
        _codeActionsSelected = 0;
    }

    // ─── Key handling ─────────────────────────────────────────────────────

    private bool HandleCodeActionKey(ConsoleKeyInfo key)
    {
        if (!_codeActionsOpen) return false;
        switch (key.Key)
        {
            case ConsoleKey.Escape:
                CloseCodeActions();
                return true;
            case ConsoleKey.Enter:
                AcceptCodeAction();
                return true;
            case ConsoleKey.UpArrow:
                if (_codeActionsSelected > 0) _codeActionsSelected--;
                return true;
            case ConsoleKey.DownArrow:
                if (_codeActionsSelected < _codeActionItems.Count - 1) _codeActionsSelected++;
                return true;
        }
        return false;
    }

    private void AcceptCodeAction()
    {
        if (!_codeActionsOpen || _codeActionsSelected >= _codeActionItems.Count)
        {
            CloseCodeActions();
            return;
        }
        var action = _codeActionItems[_codeActionsSelected];
        CloseCodeActions();
        if (action.Edit is null) { _message = $"{action.Title}: no edit provided"; return; }
        ApplyWorkspaceEdit(action.Edit, action.Title);
    }

    // ─── Edit application ─────────────────────────────────────────────────

    private void ApplyWorkspaceEdit(LspWorkspaceEdit edit, string actionTitle)
    {
        var source = string.IsNullOrEmpty(Current.FilePath) ? "untitled.tosh" : Current.FilePath;
        if (!edit.Changes.TryGetValue(source, out var textEdits) || textEdits.Count == 0)
        {
            _message = $"{actionTitle}: no edits for current file";
            return;
        }

        // Apply in reverse document order so earlier positions stay valid.
        var sorted = textEdits
            .OrderByDescending(e => e.Range.Start.Line)
            .ThenByDescending(e => e.Range.Start.Character)
            .ToList();

        foreach (var te in sorted)
        {
            var start = new TextLocation(te.Range.Start.Line, te.Range.Start.Character);
            var end = new TextLocation(te.Range.End.Line, te.Range.End.Character);
            _buffer.ClearSelection();
            _buffer.MoveCursor(start);
            if (start != end)
            {
                _buffer.BeginSelection();
                _buffer.MoveCursor(end);
                _buffer.DeleteSelection();
            }
            if (te.NewText.Length > 0)
                _buffer.InsertText(te.NewText);
        }

        _message = $"applied: {actionTitle}";
    }

    // ─── Painting ─────────────────────────────────────────────────────────

    private void PaintCodeActionsPopup(StringBuilder sb, int anchorCol, int editorTopRow, int editorHeight)
    {
        if (!_codeActionsOpen || _codeActionItems.Count == 0) return;

        var visible = Math.Min(CodeActionsMaxVisible, _codeActionItems.Count);
        var (cursorRow, _) = _view.GetCursorScreenPosition();
        var anchorRow = editorTopRow + cursorRow;  // 1-based ANSI row of the cursor

        // Width: widest item title + kind tag + padding, capped at 60.
        var width = _codeActionItems.Take(CodeActionsMaxVisible)
            .Max(a => a.Title.Length + 5);
        width = Math.Min(60, Math.Max(24, width));

        // Open below the cursor when there is room, otherwise above.
        var spaceBelow = (editorTopRow + editorHeight - 1) - anchorRow;
        var startRow = spaceBelow >= visible + 1 ? anchorRow + 1 : anchorRow - visible;
        if (startRow < editorTopRow) startRow = editorTopRow;

        var bg = TomeTheme.Active.Open(Role.PopupBg);
        var sel = TomeTheme.Active.Open(Role.PopupSelectedBg);

        for (var row = 0; row < visible; row++)
        {
            var item = _codeActionItems[row];
            // 1-based column: anchorCol is 0-based, so add 1.
            sb.Append("[").Append(startRow + row).Append(';').Append(anchorCol + 1).Append('H');
            sb.Append(row == _codeActionsSelected ? sel : bg);

            var icon = item.Kind switch { "quickfix" => "⚡", "refactor" => "⟳", _ => "·" };
            var label = $" {icon} {item.Title}";
            if (label.Length < width) label += new string(' ', width - label.Length);
            else if (label.Length > width) label = label[..(width - 1)] + "…";

            sb.Append(label).Append("[0m");
        }
    }
}
