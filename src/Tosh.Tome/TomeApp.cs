using System.Text;
using System.Text.RegularExpressions;
using Tosh.LanguageServices;
using Tosh.Tome.Settings;
using Tosh.Tome.Theme;
using Tosh.Tui.Editing;

namespace Tosh.Tome;

/// <summary>
/// The Tōme application: hosts one or more open documents as tabs, runs the
/// render/input loop, and persists files on request. Renders the full screen
/// each frame for simplicity until diffing matters.
/// </summary>
internal sealed partial class TomeApp
{
    private const int StatusLineHeight = 1;
    private const int MessageLineHeight = 1;

    private readonly TerminalDriver _terminal;
    private readonly List<Tab> _tabs = new();
    private int _active;

    private string _message = string.Empty;
    private bool _quit;

    // Active workspace, when one has been loaded via `:workspace open`.
    // Null until then; OpenFiles/Folders/Layout are consulted by the
    // explorer pane and tab restorer (future steps).
    private Tosh.Tome.Workspace.Workspace? _workspace;

    // Left-dock explorer pane. Always allocated; visible only while a
    // workspace is loaded and the pane has not been hidden via Ctrl+B
    // or the `:ws explorer hide` verb. Focus toggles with Tab.
    private readonly ExplorerPane _explorer = new();
    private bool _focusExplorer;

    // Bottom-dock embedded Tōsh REPL. Hidden until `:repl` (or `:repl open`)
    // toggles it on. Focus toggles with Ctrl+R; Esc returns focus to the
    // editor while leaving the pane visible.
    private readonly ReplPane _repl = new();
    private bool _focusRepl;

    // When true, every successful :w saves runs the formatter first.
    // Toggled via `:set format-on-save on|off`. Off by default.
    private bool _formatOnSave;

    // Modal editing — Edit (current insert-anywhere behavior) and Command
    // (vim-ish normal mode + ':' opens the command palette). Default is Edit
    // so opening Tōme feels unchanged; Esc switches to Command mode.
    private EditorMode _mode = EditorMode.Edit;

    // Bracket-match positions; either both null or both set. Recomputed in
    // Render() so the highlight follows cursor moves without explicit hooks
    // on every key handler.
    private TextLocation? _bracketMatchA;
    private TextLocation? _bracketMatchB;

    // Shared across tabs — ToshLanguageFeatures is stateless w.r.t. documents
    // and constructing one materialises a full ToshRuntime, so we do it once.
    private readonly ToshLanguageFeatures _features = new();

    // User settings (status-bar layout, glyphs, colors). Loaded once at
    // startup from $TOME_CONFIG or ~/.config/tome/settings.json.
    private readonly TomeSettings _settings = TomeSettings.Load();

    // Layout coordinates captured at the end of each Render() so the mouse
    // dispatcher can hit-test without recomputing them. All values are in
    // 0-based screen rows/columns.
    private int _layoutTabBarRow = -1;
    private int _layoutEditorTopRow;
    private int _layoutEditorHeight;
    private int _layoutLeftOffset;          // explorer + separator
    private int _layoutExplorerWidth;
    private int _layoutGutterWidth;
    private int _layoutTextLeftCol;         // first screen column inside text area
    private int _layoutStatusRow;
    private int _layoutReplTopRow = -1;
    private int _layoutReplHeight;
    private int _layoutReplLeftCol;
    private int _layoutReplWidth;
    // Tab bar hit ranges, parallel to _tabs, recorded as half-open
    // [startCol, endCol) intervals in the same row as _layoutTabBarRow.
    private readonly List<(int Start, int End)> _layoutTabRanges = new();
    private bool _mouseDragging;

    // Double-click detection for the explorer pane. We only need a single
    // "last click" — there is no overlap with other surfaces.
    private long _lastExplorerClickTickMs;
    private int _lastExplorerClickRow = -1;
    private const int DoubleClickWindowMs = 350;

    public TomeApp(TerminalDriver terminal, string? filePath, string initialText)
    {
        _terminal = terminal;
        var path = filePath ?? string.Empty;
        var tab = new Tab(path, initialText, null);
        tab.Colorizer = ResolveColorizer(tab);
        _tabs.Add(tab);
        _message = string.IsNullOrEmpty(path) ? "[new file]" : $"opened {path}";
    }

    private Tab Current => _tabs[_active];
    private TextBuffer _buffer => Current.Buffer;
    private TextEditorView _view => Current.View;

    private ISyntaxColorizer? ResolveColorizer(Tab tab)
    {
        if (string.IsNullOrEmpty(tab.FilePath)) return null;
        var ext = Path.GetExtension(tab.FilePath);
        if (string.Equals(ext, ".tosh", StringComparison.OrdinalIgnoreCase))
        {
            // Prefer the LSP-backed semantic colorizer; fall back to the lexer-only
            // implementation if anyone sets TOME_NO_LSP=1 (recovery escape hatch).
            if (Environment.GetEnvironmentVariable("TOME_NO_LSP") == "1")
                return new ToshSyntaxColorizer();
            var sourceName = string.IsNullOrEmpty(tab.FilePath) ? "untitled.tosh" : tab.FilePath;
            return new LspBackedColorizer(_features, sourceName, tab.Buffer.GetText);
        }
        // Other languages: dispatch to a tree-sitter grammar when one is
        // installed on the host (TOME_NO_TREESITTER=1 disables).
        if (Environment.GetEnvironmentVariable("TOME_NO_TREESITTER") != "1")
        {
            var lang = Tosh.Tome.TreeSitter.TreeSitterGrammarRegistry.Resolve(tab.FilePath, out var grammar);
            Tosh.Tome.TreeSitter.TreeSitterDebug.Log($"ResolveColorizer path={tab.FilePath} ext={ext} grammar={grammar ?? "<none>"} lang=0x{lang.ToInt64():x}");
            if (lang != IntPtr.Zero && grammar != null)
                return new Tosh.Tome.TreeSitter.TreeSitterColorizer(lang, grammar, tab.Buffer.GetText);
        }
        return null;
    }

    public void Run()
    {
        while (!_quit)
        {
            Render();
            var evt = _terminal.ReadEvent();
            if (evt.Kind == InputEventKind.Key) HandleKey(evt.Key);
            else HandleMouse(evt);
        }
    }

    private int TabBarHeight => _tabs.Count > 1 ? 1 : 0;

    /// <summary>
    /// Recomputes diagnostics for the current tab if the buffer text has
    /// changed since the last refresh. Cheap when the text is unchanged
    /// (string equality short-circuit) and a single parse pass otherwise.
    /// .tosh files only \u2014 other extensions skip entirely.
    /// </summary>
    private void RefreshDiagnosticsIfStale()
    {
        if (!IsToshTab()) { Current.Diagnostics = Array.Empty<LspDiagnostic>(); Current.DiagnosticsPopulated = false; return; }
        if (Environment.GetEnvironmentVariable("TOME_NO_LSP") == "1") { Current.Diagnostics = Array.Empty<LspDiagnostic>(); Current.DiagnosticsPopulated = false; return; }
        var text = _buffer.GetText();
        if (Current.DiagnosticsPopulated && string.Equals(text, Current.DiagnosticsForText, StringComparison.Ordinal))
            return;
        var source = string.IsNullOrEmpty(Current.FilePath) ? "untitled.tosh" : Current.FilePath;
        try { Current.Diagnostics = _features.GetDiagnostics(text, source); }
        catch { Current.Diagnostics = Array.Empty<LspDiagnostic>(); }
        Current.DiagnosticsForText = text;
        Current.DiagnosticsPopulated = true;
    }

    private Dictionary<int, int> BuildSeverityByLine()
    {
        var map = new Dictionary<int, int>();
        foreach (var d in Current.Diagnostics)
        {
            var line = d.Range.Start.Line;
            if (!map.TryGetValue(line, out var existing) || d.Severity < existing)
                map[line] = d.Severity;
        }
        return map;
    }

    private static bool IsOpenBracket(char c) => c is '(' or '[' or '{';
    private static bool IsCloseBracket(char c) => c is ')' or ']' or '}';
    private static char MatchingClose(char c) => c switch { '(' => ')', '[' => ']', '{' => '}', _ => '\0' };
    private static char MatchingOpen(char c) => c switch { ')' => '(', ']' => '[', '}' => '{', _ => '\0' };

    private void RecomputeBracketMatch()
    {
        _bracketMatchA = null;
        _bracketMatchB = null;
        var lineIdx = _buffer.Cursor.Line;
        var col = _buffer.Cursor.Column;
        var line = _buffer.GetLine(lineIdx);

        // Prefer char under the cursor; fall back to the char immediately
        // before. Try forward scan for openers, backward scan for closers.
        if (col < line.Length && TryStartAt(lineIdx, col, line[col])) return;
        if (col > 0 && TryStartAt(lineIdx, col - 1, line[col - 1])) return;
    }

    private bool TryStartAt(int lineIdx, int col, char c)
    {
        if (IsOpenBracket(c))
        {
            if (FindForward(lineIdx, col, c, MatchingClose(c), out var mate))
            {
                _bracketMatchA = new TextLocation(lineIdx, col);
                _bracketMatchB = mate;
                return true;
            }
        }
        else if (IsCloseBracket(c))
        {
            if (FindBackward(lineIdx, col, MatchingOpen(c), c, out var mate))
            {
                _bracketMatchA = new TextLocation(lineIdx, col);
                _bracketMatchB = mate;
                return true;
            }
        }
        return false;
    }

    private bool FindForward(int startLine, int startCol, char open, char close, out TextLocation mate)
    {
        var depth = 0;
        for (var l = startLine; l < _buffer.LineCount; l++)
        {
            var text = _buffer.GetLine(l);
            var from = l == startLine ? startCol : 0;
            for (var i = from; i < text.Length; i++)
            {
                var ch = text[i];
                if (ch == open) depth++;
                else if (ch == close)
                {
                    depth--;
                    if (depth == 0) { mate = new TextLocation(l, i); return true; }
                }
            }
        }
        mate = default;
        return false;
    }

    private bool FindBackward(int startLine, int startCol, char open, char close, out TextLocation mate)
    {
        var depth = 0;
        for (var l = startLine; l >= 0; l--)
        {
            var text = _buffer.GetLine(l);
            var from = l == startLine ? startCol : text.Length - 1;
            for (var i = from; i >= 0; i--)
            {
                var ch = text[i];
                if (ch == close) depth++;
                else if (ch == open)
                {
                    depth--;
                    if (depth == 0) { mate = new TextLocation(l, i); return true; }
                }
            }
        }
        mate = default;
        return false;
    }

    private bool TryAutoPair(char ch)
    {
        // Returns true if it consumed the keystroke. Otherwise the caller
        // proceeds with the default InsertChar path.
        if (_buffer.HasSelection) return false; // keep wrap-on-selection out of v1

        var lineIdx = _buffer.Cursor.Line;
        var col = _buffer.Cursor.Column;
        var line = _buffer.GetLine(lineIdx);
        var prev = col > 0 ? line[col - 1] : '\0';
        var next = col < line.Length ? line[col] : '\0';

        char close;
        switch (ch)
        {
            case '(': close = ')'; break;
            case '[': close = ']'; break;
            case '{': close = '}'; break;
            case '"':
                if (next == '"') { _buffer.MoveRight(); return true; }
                if (IsWordChar(prev) || IsWordChar(next)) return false;
                close = '"'; break;
            case '\'':
                if (next == '\'') { _buffer.MoveRight(); return true; }
                if (IsWordChar(prev) || IsWordChar(next)) return false;
                close = '\''; break;
            case '<':
                // Only autopair when '<' comes directly after a word
                // character — keeps math/comparison expressions clean.
                if (!IsWordChar(prev)) return false;
                close = '>'; break;
            // Closer type-over: if the user types the same close char that
            // is already at the cursor, skip it.
            case ')':
            case ']':
            case '}':
            case '>':
                if (next == ch) { _buffer.MoveRight(); return true; }
                return false;
            default:
                return false;
        }

        _buffer.InsertChar(ch);
        _buffer.InsertChar(close);
        _buffer.MoveLeft();
        return true;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private void Render()
    {
        var width = _terminal.Width;
        var height = _terminal.Height;
        var editorHeightTotal = Math.Max(1, height - StatusLineHeight - MessageLineHeight - TabBarHeight);
        var replHeight = _repl.EffectiveHeight(editorHeightTotal);
        var editorHeight = Math.Max(1, editorHeightTotal - replHeight);
        var editorTopRow = TabBarHeight + 1;

        _explorer.ConsumeChanges();
        RefreshDiagnosticsIfStale();
        RecomputeBracketMatch();
        RefreshSignatureHelp();
        CheckExternalChange();
        var gutter = new GutterRenderer(_buffer, BuildSeverityByLine());

        // Explorer pane sits flush against the left edge. When visible
        // it consumes the configured Width plus a one-column separator;
        // the gutter and editor are shifted right accordingly.
        var explorerVisible = _explorer.Open && _explorer.HasRoots;
        var explorerWidth = explorerVisible ? Math.Max(8, Math.Min(_explorer.Width, width - 20)) : 0;
        var explorerSepWidth = explorerVisible ? 1 : 0;
        var leftOffset = explorerWidth + explorerSepWidth;

        var gutterWidth = Math.Min(Math.Max(1, width - leftOffset - 1), gutter.Width);
        var textWidth = Math.Max(1, width - leftOffset - gutterWidth);

        _view.SetViewportSize(textWidth, editorHeight);
        _view.EnsureCursorVisible();

        // Stash layout coords for the mouse dispatcher.
        _layoutTabBarRow = TabBarHeight > 0 ? 0 : -1;
        _layoutEditorTopRow = editorTopRow - 1; // 0-based
        _layoutEditorHeight = editorHeight;
        _layoutLeftOffset = leftOffset;
        _layoutExplorerWidth = explorerVisible ? explorerWidth : 0;
        _layoutGutterWidth = gutterWidth;
        _layoutTextLeftCol = leftOffset + gutterWidth;
        _layoutStatusRow = (editorTopRow - 1) + editorHeight;

        var sb = new StringBuilder(width * height);

        if (TabBarHeight > 0)
        {
            sb.Append("\u001b[1;1H\u001b[2K");
            if (explorerVisible)
            {
                // Workspace name banner aligned with the explorer column.
                var banner = _workspace is null ? string.Empty : " " + _workspace.Name;
                if (banner.Length > explorerWidth) banner = banner.Substring(0, explorerWidth);
                sb.Append(TomeTheme.Active.Open(Role.StatusBarBg)).Append(banner.PadRight(explorerWidth)).Append("\u001b[0m");
                sb.Append("\u001b[2m\u2502\u001b[22m");
            }
            sb.Append(BuildTabBar(width - leftOffset));
        }

        var currentLine = _buffer.Cursor.Line;
        var selection = _buffer.Selection;

        for (var row = 0; row < editorHeight; row++)
        {
            sb.Append("\u001b[").Append(editorTopRow + row).Append(";1H\u001b[2K");

            if (explorerVisible)
            {
                sb.Append(_explorer.RenderRow(row, editorHeight, _focusExplorer));
                sb.Append("\u001b[2m\u2502\u001b[22m");
            }

            var lineIndex = _view.ScrollLine + row;
            if (lineIndex < _buffer.LineCount)
            {
                sb.Append(gutter.Render(lineIndex, lineIndex == currentLine));
                var (selStart, selEnd) = ComputeLineSelection(selection, lineIndex, _buffer.GetLineLength(lineIndex));
                AppendVisibleLine(sb, _buffer.GetLine(lineIndex), lineIndex, _view.ScrollColumn, textWidth, selStart, selEnd);
            }
            else
            {
                sb.Append(gutter.Render(lineIndex, isCurrentLine: false));
                sb.Append("\u001b[2m~\u001b[0m");
            }
        }

        var statusRow = editorTopRow + editorHeight + replHeight;
        // Paint the REPL pane (if visible) between the editor body and the
        // status row. It always sits flush right of the explorer like the
        // editor body — the explorer column is never split.
        if (replHeight > 0)
        {
            var replTop = editorTopRow + editorHeight;          // 1-based row
            var replLeftCol = leftOffset + 1;                   // 1-based col
            var replWidth = Math.Max(1, width - leftOffset);
            _layoutReplTopRow = replTop - 1;                    // 0-based
            _layoutReplHeight = replHeight;
            _layoutReplLeftCol = leftOffset;                    // 0-based
            _layoutReplWidth = replWidth;
            _repl.Render(sb, replTop, replLeftCol, replWidth, replHeight, _focusRepl);
        }
        else
        {
            _layoutReplTopRow = -1;
            _layoutReplHeight = 0;
        }

        sb.Append("\u001b[").Append(statusRow).Append(";1H\u001b[2K");
        sb.Append(BuildStatusLine(width));
        sb.Append("\u001b[0m");

        sb.Append("\u001b[").Append(statusRow + 1).Append(";1H\u001b[2K");
        // Message-line precedence: an explicit one-shot _message wins; when
        // the slot is otherwise empty, surface the active signature help.
        if (_message.Length > 0) sb.Append(_message);
        else if (_signatureHelpText.Length > 0) sb.Append("\u001b[2m").Append(_signatureHelpText).Append("\u001b[22m");

        // Popup overlay last, so it paints on top of the editor body.
        PaintCompletionPopup(sb, leftOffset + gutterWidth, editorTopRow, editorHeight, width);
        PaintFuzzyPicker(sb, width, height);

        _terminal.Write(sb.ToString());

        if (_focusExplorer)
        {
            // Park the terminal cursor at the column edge so it doesn't
            // distract; selection is rendered via reverse-video.
            _terminal.ShowCursorAt(editorTopRow, 1);
        }
        else if (_pickerOpen)
        {
            var (r, c) = GetPickerCursorScreenPosition(width, height);
            _terminal.ShowCursorAt(r, c);
        }
        else if (_focusRepl && _layoutReplHeight > 0)
        {
            var (r, c) = _repl.GetCursorScreenPosition(
                _layoutReplTopRow + 1, _layoutReplLeftCol + 1, _layoutReplWidth, _layoutReplHeight);
            _terminal.ShowCursorAt(r, c);
        }
        else
        {
            var (cursorRow, cursorCol) = _view.GetCursorScreenPosition();
            _terminal.ShowCursorAt(cursorRow + TabBarHeight, cursorCol + leftOffset + gutterWidth);

            // Paint extra carets as reverse-video block characters at their
            // viewport positions. The hardware terminal cursor can only sit
            // at one location, so secondaries are drawn as text styling.
            if (_buffer.HasMultipleCarets)
            {
                var extras = _view.GetExtraCursorScreenPositions();
                if (extras.Count > 0)
                {
                    var paint = new StringBuilder();
                    foreach (var (r, c) in extras)
                    {
                        // Mirror the body-paint coords from above: rows are
                        // 1-based screen rows (editorTopRow + viewport row),
                        // columns are 1-based screen cols (leftOffset +
                        // gutterWidth + viewport col + 1).
                        var screenRow = editorTopRow + r;
                        var screenCol = leftOffset + gutterWidth + c + 1;
                        var lineIdx = _view.ScrollLine + r;
                        var colIdx = _view.ScrollColumn + c;
                        var line = _buffer.GetLine(lineIdx);
                        var ch = (colIdx >= 0 && colIdx < line.Length) ? line[colIdx] : ' ';
                        paint.Append($"\u001b[{screenRow};{screenCol}H");
                        paint.Append("\u001b[7m").Append(ch).Append("\u001b[27m");
                    }
                    _terminal.Write(paint.ToString());
                    // Re-park the hardware cursor on the primary so the
                    // visible caret lands at the right spot.
                    _terminal.ShowCursorAt(cursorRow + TabBarHeight, cursorCol + leftOffset + gutterWidth);
                }
            }
        }
        _terminal.Flush();
    }

    private string BuildTabBar(int width)
    {
        var sb = new StringBuilder();
        _layoutTabRanges.Clear();
        var startCol = _layoutLeftOffset;
        for (var i = 0; i < _tabs.Count; i++)
        {
            var t = _tabs[i];
            var label = $" {t.DisplayName}{(t.Buffer.IsModified ? "*" : "")} ";
            if (i == _active) sb.Append("\u001b[7m").Append(label).Append("\u001b[27m");
            else sb.Append("\u001b[2m").Append(label).Append("\u001b[22m");
            _layoutTabRanges.Add((startCol, startCol + label.Length));
            startCol += label.Length;
            if (i < _tabs.Count - 1) { sb.Append('|'); startCol += 1; }
        }
        var rendered = sb.ToString();
        // Trim ANSI-aware: simple length cap with overflow truncation is fine here for the bar.
        return rendered;
    }

    private string BuildStatusLine(int width)
    {
        int? selLen = null;
        if (_buffer.Selection is not null)
        {
            var len = _buffer.GetSelectionText().Length;
            if (len > 0) selLen = len;
        }
        var inputs = new StatusBar.Inputs(
            Mode: _mode,
            FilePath: Current.FilePath ?? string.Empty,
            DisplayName: Current.DisplayName,
            IsModified: _buffer.IsModified,
            FocusExplorer: _focusExplorer,
            WorkspaceName: _workspace?.Name,
            Cursor: _buffer.Cursor,
            LineCount: _buffer.LineCount,
            SelectionLength: selLen,
            Diagnostics: Current.Diagnostics ?? Array.Empty<LspDiagnostic>(), ActiveTab: _active,
            TabCount: _tabs.Count);
        return StatusBar.Render(inputs, _settings, width);
    }

    private void HandleKey(ConsoleKeyInfo key)
    {
        _message = string.Empty;
        _terminal.HideCursor();

        // Picker overlay consumes everything until dismissed.
        if (_pickerOpen) { HandlePickerKey(key); return; }

        // *Results* tab intercepts Enter to jump to the match under cursor.
        if (TryHandleResultsTabKey(key)) return;

        // Global: Ctrl+P opens the fuzzy picker regardless of mode/focus.
        if (key.Key == ConsoleKey.P && (key.Modifiers & ConsoleModifiers.Control) != 0)
        {
            OpenFuzzyPicker();
            return;
        }

        // Global: Ctrl+B toggles the explorer pane regardless of mode/focus.
        if (key.Key == ConsoleKey.B && (key.Modifiers & ConsoleModifiers.Control) != 0)
        {
            if (!_explorer.HasRoots)
            {
                _message = "explorer: no workspace loaded";
                return;
            }
            if (_explorer.Open && _focusExplorer)
            {
                _explorer.Open = false;
                _focusExplorer = false;
                _message = "explorer hidden";
            }
            else if (_explorer.Open)
            {
                _focusExplorer = true;
                _message = "-- EXPLORER --";
            }
            else
            {
                _explorer.Open = true;
                _focusExplorer = true;
                _message = "-- EXPLORER --";
            }
            return;
        }

        // Global: Ctrl+R toggles focus to/from the embedded REPL pane.
        // Opens the pane if it isn't visible yet.
        if (key.Key == ConsoleKey.R && (key.Modifiers & ConsoleModifiers.Control) != 0)
        {
            var cwd = !string.IsNullOrEmpty(Current.FilePath)
                ? Path.GetDirectoryName(Path.GetFullPath(Current.FilePath))
                : Environment.CurrentDirectory;
            if (!_repl.Visible) { _repl.Open(cwd); _focusRepl = true; _message = "-- REPL --"; }
            else if (_focusRepl) { _focusRepl = false; _message = string.Empty; }
            else { _focusRepl = true; _message = "-- REPL --"; }
            return;
        }

        // When the explorer has focus, the editor sees no input until
        // focus returns. Tab or Escape returns focus to the editor.
        if (_focusExplorer)
        {
            HandleExplorerKey(key);
            return;
        }

        // When the REPL has focus, it consumes everything until Esc.
        if (_focusRepl && _repl.Visible)
        {
            if (!_repl.HandleKey(key))
            {
                _focusRepl = false;
                _message = string.Empty;
            }
            return;
        }

        if (_mode == EditorMode.Command)
        {
            HandleCommandModeKey(key);
            return;
        }

        // Escape from Edit mode drops into Command mode (vim-style). Any
        // active selection is cleared so the user sees a clean cursor.
        // BUT: if there are extra carets, the first Esc collapses them
        // and stays in Edit mode — second press then enters Command mode.
        if (key.Key == ConsoleKey.Escape)
        {
            if (_buffer.HasMultipleCarets)
            {
                var n = _buffer.ExtraCaretCount;
                _buffer.ClearExtraCarets();
                _message = $"collapsed {n} extra caret(s)";
                return;
            }
            _buffer.ClearSelection();
            _mode = EditorMode.Command;
            _message = "-- COMMAND --";
            return;
        }

        HandleEditModeKey(key);
    }

    private void HandleEditModeKey(ConsoleKeyInfo key)
    {
        // Completion popup intercepts most navigation / commit keys.
        if (_completionOpen && HandleCompletionKey(key)) return;

        var ctrl = (key.Modifiers & ConsoleModifiers.Control) != 0;
        var shift = (key.Modifiers & ConsoleModifiers.Shift) != 0;
        var alt = (key.Modifiers & ConsoleModifiers.Alt) != 0;

        // Ctrl+Space — terminals report this inconsistently: some send
        //   {Key=Spacebar, KeyChar='\0', Mod=Control}, others send a bare
        //   NUL byte {Key=0, KeyChar='\0', Mod=0}, and a few send the plain
        //   Spacebar with the Control flag. Detect all three before falling
        //   through to the rest of the dispatch table.
        if ((key.KeyChar == '\0' && (key.Key == ConsoleKey.Spacebar || key.Key == 0)) ||
            (ctrl && key.Key == ConsoleKey.Spacebar))
        {
            OpenCompletions();
            return;
        }

        // Ctrl+Alt+Up/Down and Alt+Shift+Up/Down — add caret above / below the
        // primary. Some terminals/WMs swallow Ctrl+Alt+Arrow (i3, KDE, etc.),
        // so Alt+Shift is offered as a reliable fallback.
        if ((ctrl && alt) || (alt && shift))
        {
            if (key.Key == ConsoleKey.UpArrow) { AddCaretAbove(); return; }
            if (key.Key == ConsoleKey.DownArrow) { AddCaretBelow(); return; }
        }

        if (alt)
        {
            switch (key.Key)
            {
                case ConsoleKey.G: GotoLine(); return;
                case ConsoleKey.D: ShowDiagnostics(); return;
                case ConsoleKey.Spacebar: OpenCompletions(); return;
            }
        }

        if (ctrl)
        {
            switch (key.Key)
            {
                case ConsoleKey.Q: TryQuit(); return;
                case ConsoleKey.S: Save(); return;
                case ConsoleKey.O: OpenFile(); return;
                case ConsoleKey.T: NewTab(); return;
                case ConsoleKey.W: CloseTab(); return;
                case ConsoleKey.PageUp: SwitchTab(-1); return;
                case ConsoleKey.PageDown: SwitchTab(+1); return;
                case ConsoleKey.Z: _buffer.Undo(); return;
                case ConsoleKey.Y: _buffer.Redo(); return;
                case ConsoleKey.A: SelectAll(); return;
                case ConsoleKey.E: ApplyMove(_buffer.MoveLineEnd, shift); return;
                case ConsoleKey.F: StartSearch(); return;
                case ConsoleKey.G: FindNext(); return;
                case ConsoleKey.R: StartInteractiveReplace(); return;
                case ConsoleKey.K: ShowHover(); return;
                case ConsoleKey.Spacebar: OpenCompletions(); return;
                case ConsoleKey.C: CopySelection(); return;
                case ConsoleKey.X: CutSelection(); return;
                case ConsoleKey.V: Paste(); return;
                case ConsoleKey.LeftArrow: ApplyMove(_buffer.MoveWordLeft, shift); return;
                case ConsoleKey.RightArrow: ApplyMove(_buffer.MoveWordRight, shift); return;
                case ConsoleKey.Backspace: DeleteWordLeft(); return;
                case ConsoleKey.Delete: DeleteWordRight(); return;
            }
        }

        if (key.Key == ConsoleKey.F3) { FindNext(); return; }

        switch (key.Key)
        {
            case ConsoleKey.LeftArrow: ApplyMove(_buffer.MoveLeft, shift); return;
            case ConsoleKey.RightArrow: ApplyMove(_buffer.MoveRight, shift); return;
            case ConsoleKey.UpArrow: ApplyMove(_buffer.MoveUp, shift); return;
            case ConsoleKey.DownArrow: ApplyMove(_buffer.MoveDown, shift); return;
            case ConsoleKey.Home: ApplyMove(_buffer.MoveLineStart, shift); return;
            case ConsoleKey.End: ApplyMove(_buffer.MoveLineEnd, shift); return;
            case ConsoleKey.PageUp: ApplyMove(() => PageBy(-_view.ViewportHeight), shift); return;
            case ConsoleKey.PageDown: ApplyMove(() => PageBy(_view.ViewportHeight), shift); return;
            case ConsoleKey.Backspace:
                EditAll(b => { if (b.HasSelection) b.DeleteSelection(); else b.Backspace(); });
                return;
            case ConsoleKey.Delete:
                EditAll(b => { if (b.HasSelection) b.DeleteSelection(); else b.DeleteForward(); });
                return;
            case ConsoleKey.Enter:
                EditAll(b => { if (b.HasSelection) b.DeleteSelection(); b.InsertNewline(); });
                return;
            case ConsoleKey.Tab:
                EditAll(b => { if (b.HasSelection) b.DeleteSelection(); b.InsertText("    "); });
                return;
        }

        if (!char.IsControl(key.KeyChar))
        {
            // Auto-pair only makes sense at the primary caret; with extras
            // we just insert the literal char everywhere.
            if (!_buffer.HasMultipleCarets && TryAutoPair(key.KeyChar))
            { TryRefilterCompletions(); return; }
            var ch = key.KeyChar;
            EditAll(b => { if (b.HasSelection) b.DeleteSelection(); b.InsertChar(ch); });
            TryRefilterCompletions();
        }
    }

    /// <summary>
    /// Run <paramref name="op"/> at every active caret as a single undo
    /// transaction. Falls through to a plain call when there's only one
    /// caret, so undo coalescing still works for ordinary typing.
    /// </summary>
    private void EditAll(Action<TextBuffer> op)
    {
        if (_buffer.HasMultipleCarets) _buffer.ApplyAtAllCarets(op);
        else op(_buffer);
    }

    /// <summary>
    /// Add an extra caret one line above the topmost existing caret,
    /// using the primary caret's column as the anchor so each successive
    /// press grows the column vertically rather than staying pinned to
    /// the primary's line.
    /// </summary>
    private void AddCaretAbove()
    {
        var anchorCol = _buffer.Cursor.Column;
        var topLine = int.MaxValue;
        foreach (var c in _buffer.AllCarets)
            if (c.Line < topLine) topLine = c.Line;
        if (topLine <= 0) { _message = "no line above"; return; }
        var targetLine = topLine - 1;
        var target = new TextLocation(targetLine,
            Math.Min(anchorCol, _buffer.GetLineLength(targetLine)));
        _buffer.AddCaret(target);
        _message = $"{_buffer.ExtraCaretCount + 1} carets";
    }

    /// <summary>
    /// Add an extra caret one line below the bottommost existing caret
    /// (see <see cref="AddCaretAbove"/> for column-anchor rationale).
    /// </summary>
    private void AddCaretBelow()
    {
        var anchorCol = _buffer.Cursor.Column;
        var bottomLine = -1;
        foreach (var c in _buffer.AllCarets)
            if (c.Line > bottomLine) bottomLine = c.Line;
        if (bottomLine + 1 >= _buffer.LineCount) { _message = "no line below"; return; }
        var targetLine = bottomLine + 1;
        var target = new TextLocation(targetLine,
            Math.Min(anchorCol, _buffer.GetLineLength(targetLine)));
        _buffer.AddCaret(target);
        _message = $"{_buffer.ExtraCaretCount + 1} carets";
    }

    private void ApplyMove(Action move, bool extend)
    {
        if (_buffer.HasMultipleCarets)
        {
            // Fan a movement out across every caret. The `move` delegate
            // operates on whatever the current primary is, so swapping each
            // caret in turn produces the same motion at every site.
            _buffer.ApplyAtAllCarets(_ =>
            {
                if (extend) _buffer.BeginSelection();
                else _buffer.ClearSelection();
                move();
            });
            return;
        }
        if (extend) _buffer.BeginSelection();
        else _buffer.ClearSelection();
        move();
    }

    private void SelectAll()
    {
        _buffer.MoveCursor(new TextLocation(0, 0));
        _buffer.BeginSelection();
        _buffer.MoveCursor(new TextLocation(_buffer.LineCount - 1, _buffer.GetLineLength(_buffer.LineCount - 1)));
    }

    private void CopySelection()
    {
        var text = _buffer.GetSelectionText();
        if (text.Length == 0) { _message = "nothing selected"; return; }
        Clipboard.SetText(text);
        _message = $"copied {text.Length} char(s)";
    }

    private void CutSelection()
    {
        if (!_buffer.HasSelection) { _message = "nothing selected"; return; }
        var deleted = _buffer.DeleteSelection();
        Clipboard.SetText(deleted);
        _message = $"cut {deleted.Length} char(s)";
    }

    private void Paste()
    {
        var text = Clipboard.GetText();
        if (string.IsNullOrEmpty(text)) { _message = "clipboard empty"; return; }
        if (_buffer.HasSelection) _buffer.DeleteSelection();
        _buffer.InsertText(text);
        _message = $"pasted {text.Length} char(s)";
    }

    private void DeleteWordLeft()
    {
        if (_buffer.HasSelection) { _buffer.DeleteSelection(); return; }
        _buffer.BeginSelection();
        _buffer.MoveWordLeft();
        _buffer.DeleteSelection();
    }

    private void DeleteWordRight()
    {
        if (_buffer.HasSelection) { _buffer.DeleteSelection(); return; }
        _buffer.BeginSelection();
        _buffer.MoveWordRight();
        _buffer.DeleteSelection();
    }

    // ─── Tab management ──────────────────────────────────────────────────

    private void NewTab()
    {
        _tabs.Add(new Tab(string.Empty, string.Empty, colorizer: null));
        _active = _tabs.Count - 1;
        _message = "new tab";
    }

    private void CloseTab()
    {
        if (_buffer.IsModified)
        {
            var confirm = PromptText("unsaved changes — close tab? (y/N) ");
            if (confirm is not { Length: > 0 } || (confirm[0] != 'y' && confirm[0] != 'Y'))
            {
                _message = "close cancelled";
                return;
            }
        }
        if (_tabs.Count == 1)
        {
            // Replace the last tab with a fresh empty buffer rather than exit.
            _tabs[0] = new Tab(string.Empty, string.Empty, colorizer: null);
            _active = 0;
            _message = "buffer cleared";
            return;
        }
        _tabs.RemoveAt(_active);
        if (_active >= _tabs.Count) _active = _tabs.Count - 1;
        _message = "tab closed";
    }

    private void SwitchTab(int delta)
    {
        if (_tabs.Count <= 1) return;
        _active = (_active + delta + _tabs.Count) % _tabs.Count;
    }

    private void HandleMouse(InputEvent evt)
    {
        _message = string.Empty;

        var editorBottom = _layoutEditorTopRow + _layoutEditorHeight;
        var inEditorRows = evt.Row >= _layoutEditorTopRow && evt.Row < editorBottom;
        var explorerVisible = _layoutExplorerWidth > 0;
        var inExplorer = explorerVisible && inEditorRows && evt.Column < _layoutExplorerWidth;

        // Mouse wheel: scrolls whichever pane the pointer is over. Defaults
        // to the editor when not over the explorer.
        if (evt.Kind == InputEventKind.MouseWheel)
        {
            if (inExplorer)
                _explorer.ScrollBy(-evt.WheelDelta * 3, _layoutEditorHeight);
            else
                _view.ScrollBy(-evt.WheelDelta * 3);
            return;
        }

        // Explorer click — single click selects + focuses; double click on
        // the same row activates (toggle directory / open file). Release
        // and move events inside the explorer are ignored.
        if (inExplorer)
        {
            if (evt.Kind == InputEventKind.MousePress && evt.Button == MouseButton.Left)
            {
                var visibleRow = evt.Row - _layoutEditorTopRow;
                if (!_explorer.SelectAtRow(visibleRow)) return;
                _focusExplorer = true;
                _focusRepl = false;
                _mouseDragging = false;
                var now = Environment.TickCount64;
                if (_lastExplorerClickRow == visibleRow
                    && now - _lastExplorerClickTickMs <= DoubleClickWindowMs)
                {
                    OpenSelectedExplorerEntry();
                    _lastExplorerClickRow = -1;
                    _lastExplorerClickTickMs = 0;
                }
                else
                {
                    _lastExplorerClickRow = visibleRow;
                    _lastExplorerClickTickMs = now;
                }
            }
            return;
        }

        // Tab bar click — switch to the clicked tab. Press only; ignore
        // release/move on the tab bar.
        if (evt.Kind == InputEventKind.MousePress && evt.Button == MouseButton.Left
            && _layoutTabBarRow >= 0 && evt.Row == _layoutTabBarRow)
        {
            for (var i = 0; i < _layoutTabRanges.Count; i++)
            {
                var (s, e) = _layoutTabRanges[i];
                if (evt.Column >= s && evt.Column < e) { _active = i; return; }
            }
            return;
        }

        // Editor area: translate screen coords to buffer coords.
        var inEditor = inEditorRows && evt.Column >= _layoutTextLeftCol;

        // REPL pane click — transfer focus on press.
        if (_layoutReplHeight > 0
            && evt.Row >= _layoutReplTopRow
            && evt.Row < _layoutReplTopRow + _layoutReplHeight
            && evt.Column >= _layoutReplLeftCol)
        {
            if (evt.Kind == InputEventKind.MousePress && evt.Button == MouseButton.Left)
            {
                _focusRepl = true;
                _focusExplorer = false;
                _message = "-- REPL --";
            }
            return;
        }

        if (!inEditor)
        {
            // A press outside the editor cancels any in-progress drag.
            if (evt.Kind == InputEventKind.MousePress) _mouseDragging = false;
            return;
        }

        if (evt.Button != MouseButton.Left && evt.Kind != InputEventKind.MouseMove)
            return;

        // Move focus back to the editor if the explorer had it.
        if (_focusExplorer) _focusExplorer = false;

        var bufLine = _view.ScrollLine + (evt.Row - _layoutEditorTopRow);
        bufLine = Math.Clamp(bufLine, 0, Math.Max(0, _buffer.LineCount - 1));
        var bufCol = _view.ScrollColumn + (evt.Column - _layoutTextLeftCol);
        bufCol = Math.Clamp(bufCol, 0, _buffer.GetLineLength(bufLine));
        var target = new TextLocation(bufLine, bufCol);

        switch (evt.Kind)
        {
            case InputEventKind.MousePress:
                if (evt.Alt)
                {
                    // Alt+click adds an extra caret at the click position,
                    // leaving the primary where it was. Selection is cleared
                    // first so we don't end up with a stretched primary
                    // selection bridging both points.
                    _buffer.ClearSelection();
                    _buffer.AddCaret(target);
                    _message = $"{_buffer.ExtraCaretCount + 1} carets";
                    break;
                }
                if (evt.Shift)
                {
                    _buffer.BeginSelection();
                }
                else
                {
                    _buffer.ClearSelection();
                    _buffer.ClearExtraCarets();
                }
                _buffer.MoveCursor(target);
                _mouseDragging = true;
                break;
            case InputEventKind.MouseMove:
                if (_mouseDragging)
                {
                    _buffer.BeginSelection();
                    _buffer.MoveCursor(target);
                }
                break;
            case InputEventKind.MouseRelease:
                _mouseDragging = false;
                break;
        }
    }

    private void OpenFile()
    {
        var path = PromptText("open: ", filesystemComplete: true);
        if (string.IsNullOrWhiteSpace(path))
        {
            _message = "open cancelled";
            return;
        }
        var resolved = Path.GetFullPath(path.Trim());
        string text;
        try
        {
            text = File.Exists(resolved) ? File.ReadAllText(resolved) : string.Empty;
        }
        catch (Exception ex)
        {
            _message = $"open failed: {ex.Message}";
            return;
        }
        var openedTab = new Tab(resolved, text, null);
        openedTab.Colorizer = ResolveColorizer(openedTab);
        _tabs.Add(openedTab);
        _active = _tabs.Count - 1;
        _message = File.Exists(resolved) ? $"opened {resolved}" : $"new file: {resolved}";
    }

    private void GotoLine()
    {
        var input = PromptText("goto line[:col]: ");
        if (string.IsNullOrWhiteSpace(input)) { _message = "goto cancelled"; return; }
        var parts = input.Trim().Split(':', 2);
        if (!int.TryParse(parts[0], out var line) || line < 1)
        {
            _message = $"bad line number: {parts[0]}";
            return;
        }
        var col = 1;
        if (parts.Length == 2 && !int.TryParse(parts[1], out col)) col = 1;
        var li = Math.Min(line - 1, _buffer.LineCount - 1);
        var ci = Math.Max(0, Math.Min(col - 1, _buffer.GetLineLength(li)));
        _buffer.ClearSelection();
        _buffer.MoveCursor(new TextLocation(li, ci));
        _message = $"jumped to {li + 1}:{ci + 1}";
    }

    private void PageBy(int delta)
    {
        for (var i = 0; i < Math.Abs(delta); i++)
        {
            if (delta < 0) _buffer.MoveUp();
            else _buffer.MoveDown();
        }
    }

    private static (int Start, int End) ComputeLineSelection((TextLocation Start, TextLocation End)? selection, int lineIndex, int lineLength)
    {
        if (selection is null) return (-1, -1);
        var (start, end) = selection.Value;
        if (lineIndex < start.Line || lineIndex > end.Line) return (-1, -1);

        var s = lineIndex == start.Line ? start.Column : 0;
        var e = lineIndex == end.Line ? end.Column : lineLength + 1;
        return (s, e);
    }

    private void AppendVisibleLine(StringBuilder sb, string line, int lineIndex, int scrollColumn, int viewportWidth, int selStart, int selEnd)
    {
        var spans = Current.Colorizer?.Colorize(line, lineIndex) ?? Array.Empty<StyledSpan>();
        var trailingStart = ComputeTrailingWsStart(line, lineIndex);
        var matchCol = -1;
        if (_bracketMatchA is { } a && a.Line == lineIndex) matchCol = a.Column;
        else if (_bracketMatchB is { } b && b.Line == lineIndex) matchCol = b.Column;
        var hasSel = selStart >= 0 && selEnd > selStart;
        var newlineSelected = hasSel && selEnd > line.Length;
        var isCurrent = lineIndex == _buffer.Cursor.Line;
        var deco = new LineRenderer.LineDecorations(
            SelStart: hasSel ? selStart : -1,
            SelEnd: hasSel ? selEnd : -1,
            BracketMatchCol: matchCol,
            TrailingWsStart: trailingStart,
            IsCurrentLine: isCurrent,
            NewlineSelected: newlineSelected);
        LineRenderer.RenderLine(sb, line, lineIndex, scrollColumn, viewportWidth, spans, deco);
    }

    private int ComputeTrailingWsStart(string line, int lineIndex)
    {
        // Skip the trailing-WS dimming on the line the cursor is currently
        // editing — flickering dots while the user is mid-type are noisy.
        if (lineIndex == _buffer.Cursor.Line) return line.Length;
        var i = line.Length;
        while (i > 0 && (line[i - 1] == ' ' || line[i - 1] == '\t')) i--;
        return i == line.Length ? line.Length : i;
    }

    private void TryQuit()
    {
        var dirty = _tabs.Any(t => t.Buffer.IsModified);
        if (!dirty)
        {
            _quit = true;
            return;
        }

        var response = PromptText("unsaved changes — quit anyway? (y/N) ");
        if (response is { Length: > 0 } && (response[0] == 'y' || response[0] == 'Y'))
        {
            _quit = true;
        }
        else
        {
            _message = "quit cancelled";
        }
    }

    // ─── Search ──────────────────────────────────────────────────────────

    private void StartSearch()
    {
        var initialCursor = _buffer.Cursor;
        var query = PromptIncrementalSearch(initialCursor);
        if (query is null)
        {
            _buffer.MoveCursor(initialCursor);
            _message = "search cancelled";
            return;
        }
        if (query.Length == 0)
        {
            _message = "search cancelled";
            return;
        }
        Current.LastSearch = query;
    }

    private string? PromptIncrementalSearch(TextLocation origin)
    {
        var input = new StringBuilder();
        while (true)
        {
            Render();
            DrawPrompt(BuildSearchLabel(), input.ToString());

            var key = _terminal.ReadKey();
            if (key.Key == ConsoleKey.Escape) return null;
            if (key.Key == ConsoleKey.Enter) return input.ToString();
            if (key.Key == ConsoleKey.Backspace)
            {
                if (input.Length > 0) input.Length--;
            }
            else if ((key.Modifiers & ConsoleModifiers.Control) != 0)
            {
                if (key.Key == ConsoleKey.G)
                {
                    if (input.Length > 0) FindFrom(_buffer.Cursor, input.ToString(), includeCurrent: false);
                    continue;
                }
                if (key.Key == ConsoleKey.R)
                {
                    Current.SearchRegex = !Current.SearchRegex;
                    if (input.Length > 0) FindFrom(origin, input.ToString(), includeCurrent: true);
                    continue;
                }
                if (key.Key == ConsoleKey.I)
                {
                    Current.SearchIgnoreCase = !Current.SearchIgnoreCase;
                    if (input.Length > 0) FindFrom(origin, input.ToString(), includeCurrent: true);
                    continue;
                }
                continue;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                input.Append(key.KeyChar);
            }
            else
            {
                continue;
            }

            if (input.Length > 0)
                FindFrom(origin, input.ToString(), includeCurrent: true);
            else
                _buffer.MoveCursor(origin);
        }
    }

    private string BuildSearchLabel()
    {
        var flags = new StringBuilder();
        if (Current.SearchRegex) flags.Append('r');
        if (Current.SearchIgnoreCase) flags.Append('i');
        var tag = flags.Length > 0 ? $" [{flags}]" : string.Empty;
        return $"search{tag}: ";
    }

    private void FindNext()
    {
        if (string.IsNullOrEmpty(Current.LastSearch))
        {
            _message = "no previous search";
            return;
        }
        FindFrom(_buffer.Cursor, Current.LastSearch, includeCurrent: false);
    }

    private void FindFrom(TextLocation start, string query, bool includeCurrent)
    {
        Regex? rx = null;
        if (Current.SearchRegex)
        {
            try
            {
                var opts = RegexOptions.CultureInvariant;
                if (Current.SearchIgnoreCase) opts |= RegexOptions.IgnoreCase;
                rx = new Regex(query, opts);
            }
            catch (Exception ex)
            {
                _message = $"bad regex: {ex.Message}";
                return;
            }
        }
        var cmp = Current.SearchIgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        var startLine = start.Line;
        var startCol = includeCurrent ? start.Column : start.Column + 1;

        for (var pass = 0; pass < 2; pass++)
        {
            var firstLine = pass == 0 ? startLine : 0;
            var lastLine = pass == 0 ? _buffer.LineCount - 1 : startLine;

            for (var i = firstLine; i <= lastLine && i < _buffer.LineCount; i++)
            {
                var line = _buffer.GetLine(i);
                var from = (pass == 0 && i == startLine) ? Math.Min(startCol, line.Length) : 0;
                if (from > line.Length) continue;
                var idx = rx is null
                    ? line.IndexOf(query, from, cmp)
                    : (rx.Match(line, from) is { Success: true } m ? m.Index : -1);
                if (idx >= 0)
                {
                    _buffer.MoveCursor(new TextLocation(i, idx));
                    _message = pass == 1 ? $"wrapped to match at {i + 1}:{idx + 1}" : $"match at {i + 1}:{idx + 1}";
                    return;
                }
            }
            startCol = 0;
        }

        _message = $"not found: {query}";
    }

    // ─── Prompt with optional filesystem completion ──────────────────────

    private string? PromptText(string label, bool filesystemComplete = false)
    {
        var input = new StringBuilder();
        while (true)
        {
            DrawPrompt(label, input.ToString());
            var key = _terminal.ReadKey();
            if (key.Key == ConsoleKey.Escape) return null;
            if (key.Key == ConsoleKey.Enter) return input.ToString();
            if (key.Key == ConsoleKey.Backspace)
            {
                if (input.Length > 0) input.Length--;
                continue;
            }
            if (filesystemComplete && key.Key == ConsoleKey.Tab)
            {
                var completed = TryCompletePath(input.ToString());
                if (completed is not null)
                {
                    input.Clear();
                    input.Append(completed);
                }
                continue;
            }
            if (!char.IsControl(key.KeyChar)) input.Append(key.KeyChar);
        }
    }

    /// <summary>
    /// Best-effort filesystem completion. Returns the unique completion or the
    /// longest common prefix among matches; null when no entries match.
    /// </summary>
    private static string? TryCompletePath(string input)
    {
        if (string.IsNullOrEmpty(input)) input = "./";
        string expanded = input.StartsWith('~')
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), input[1..].TrimStart('/'))
            : input;
        var dir = Path.GetDirectoryName(expanded);
        if (string.IsNullOrEmpty(dir)) dir = expanded.EndsWith('/') ? expanded : ".";
        var prefix = expanded.EndsWith('/') ? string.Empty : Path.GetFileName(expanded);
        if (!Directory.Exists(dir)) return null;
        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(dir)
                .Select(Path.GetFileName)
                .Where(name => name is not null && name.StartsWith(prefix, StringComparison.Ordinal))
                .Select(name => name!)
                .OrderBy(name => name)
                .ToArray();
        }
        catch
        {
            return null;
        }
        if (entries.Length == 0) return null;

        var common = LongestCommonPrefix(entries);
        var pick = entries.Length == 1 ? entries[0] : common;
        if (pick.Length <= prefix.Length) pick = prefix; // no extension possible

        var dirPart = expanded.EndsWith('/') ? expanded : (dir.EndsWith('/') ? dir : dir + "/");
        var result = dirPart + pick;
        if (entries.Length == 1 && Directory.Exists(Path.Combine(dir, pick)) && !result.EndsWith('/'))
            result += "/";
        return result;
    }

    private static string LongestCommonPrefix(string[] entries)
    {
        if (entries.Length == 0) return string.Empty;
        var prefix = entries[0];
        for (var i = 1; i < entries.Length; i++)
        {
            var s = entries[i];
            var n = Math.Min(prefix.Length, s.Length);
            var k = 0;
            while (k < n && prefix[k] == s[k]) k++;
            prefix = prefix[..k];
            if (prefix.Length == 0) break;
        }
        return prefix;
    }

    private void DrawPrompt(string label, string value)
    {
        var row = Math.Max(1, _terminal.Height);
        var sb = new StringBuilder();
        sb.Append("\u001b[").Append(row).Append(";1H\u001b[2K");
        sb.Append(label).Append(value);
        _terminal.Write(sb.ToString());
        _terminal.ShowCursorAt(row, label.Length + value.Length + 1);
        _terminal.Flush();
    }

    private void Save()
    {
        if (string.IsNullOrEmpty(Current.FilePath))
        {
            var path = PromptText("save as: ", filesystemComplete: true);
            if (string.IsNullOrWhiteSpace(path))
            {
                _message = "save cancelled";
                return;
            }
            Current.FilePath = Path.GetFullPath(path.Trim());
            Current.Colorizer = ResolveColorizer(Current);
        }

        try
        {
            if (_formatOnSave && Formatter.HasFormatterFor(Current.FilePath))
            {
                var pre = _buffer.GetText();
                var fr = Formatter.Format(Current.FilePath, pre);
                if (fr.Ok && !string.Equals(fr.Text, pre, StringComparison.Ordinal))
                {
                    var savedCursor = _buffer.Cursor;
                    _buffer.ReplaceAll(fr.Text);
                    _buffer.MoveCursor(savedCursor);
                }
            }
            File.WriteAllText(Current.FilePath, _buffer.GetText());
            _buffer.MarkClean();
            PersistentUndoStore.Save(Current.FilePath, _buffer);
            Current.StampFromDisk();
            _message = $"saved {Current.FilePath}";
        }
        catch (Exception ex)
        {
            _message = $"save failed: {ex.Message}";
        }
    }

    private bool IsToshTab()
    {
        var path = Current.FilePath;
        if (string.IsNullOrEmpty(path)) return true; // untitled — assume .tosh
        return string.Equals(Path.GetExtension(path), ".tosh", StringComparison.OrdinalIgnoreCase);
    }

    private void ShowHover()
    {
        if (!IsToshTab()) { _message = "hover: not a .tosh file"; return; }
        var pos = new LspPosition(_buffer.Cursor.Line, _buffer.Cursor.Column);
        var source = string.IsNullOrEmpty(Current.FilePath) ? "untitled.tosh" : Current.FilePath;
        LspHover? hover;
        try { hover = _features.GetHover(_buffer.GetText(), source, pos); }
        catch (Exception ex) { _message = $"hover failed: {ex.Message}"; return; }
        if (hover is null) { _message = "no hover info"; return; }
        // Collapse the hover markdown to a single status-line summary; the
        // message line only renders one line so multi-line markdown gets
        // joined with " \u2502 ".
        var raw = hover.Contents.Value ?? string.Empty;
        var lines = raw.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        _message = lines.Length == 0 ? "no hover info" : string.Join("  \u2502  ", lines);
    }

    private void ShowDiagnostics()
    {
        if (!IsToshTab()) { _message = "diagnostics: not a .tosh file"; return; }
        RefreshDiagnosticsIfStale();
        var diags = Current.Diagnostics;
        if (diags.Count == 0) { _message = "no diagnostics"; return; }

        // Find the diagnostic at or after the cursor; wrap to the first.
        var cursor = _buffer.Cursor;
        LspDiagnostic? target = null;
        foreach (var d in diags)
        {
            if (d.Range.Start.Line > cursor.Line ||
                (d.Range.Start.Line == cursor.Line && d.Range.Start.Character >= cursor.Column))
            {
                target = d; break;
            }
        }
        target ??= diags[0];
        _buffer.MoveCursor(new TextLocation(target.Range.Start.Line, target.Range.Start.Character));
        var sev = target.Severity switch { 1 => "error", 2 => "warn", 3 => "info", _ => "hint" };
        var idx = 1;
        for (var i = 0; i < diags.Count; i++) if (ReferenceEquals(diags[i], target)) { idx = i + 1; break; }
        _message = $"[{idx}/{diags.Count} {sev}] {target.Code}: {target.Message}";
    }
}
