using System.Text;
using Tosh.LanguageServices;
using Tosh.Tome.Theme;
using Tosh.Tui.Editing;

namespace Tosh.Tome;

/// <summary>
/// Centered modal fuzzy picker for files (default) and document symbols
/// (when the query begins with <c>@</c>). Opens with Ctrl+P or the
/// <c>:files</c> / <c>:p</c> palette verbs.
/// </summary>
internal sealed partial class TomeApp
{
    private enum PickerKind { File, Symbol }

    private readonly record struct PickerItem(PickerKind Kind, string Display, string Hint, string Target, int SymbolLine, int SymbolCol);

    private bool _pickerOpen;
    private string _pickerQuery = string.Empty;
    private List<PickerItem> _pickerFiles = new();
    private List<PickerItem> _pickerSymbols = new();
    private List<(PickerItem Item, int Score)> _pickerFiltered = new();
    private int _pickerSelected;
    private int _pickerScroll;

    private const int PickerMaxVisible = 14;
    private const int PickerMaxFiles = 5000;

    private void OpenFuzzyPicker()
    {
        _pickerQuery = string.Empty;
        _pickerSelected = 0;
        _pickerScroll = 0;
        _pickerFiles = CollectPickerFiles();
        _pickerSymbols = CollectPickerSymbols();
        _pickerOpen = true;
        RefilterPicker();
        _message = string.Empty;
    }

    private void ClosePicker()
    {
        _pickerOpen = false;
        _pickerQuery = string.Empty;
        _pickerFiltered.Clear();
        _pickerSelected = 0;
        _pickerScroll = 0;
    }

    private List<PickerItem> CollectPickerFiles()
    {
        var items = new List<PickerItem>(256);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Recent tabs first (file-backed only), in current ordering.
        foreach (var t in _tabs)
        {
            if (string.IsNullOrEmpty(t.FilePath)) continue;
            if (!seen.Add(t.FilePath)) continue;
            items.Add(new PickerItem(PickerKind.File, Path.GetFileName(t.FilePath), t.FilePath, t.FilePath, 0, 0));
        }
        var openTabsEnd = items.Count;

        var roots = ResolveSearchRoots();
        var workspaceExcludes = _workspace?.Exclude ?? Array.Empty<string>();
        foreach (var root in roots)
        {
            if (items.Count >= PickerMaxFiles) break;
            WalkAndCollect(root, workspaceExcludes, items, seen);
        }

        // Sort the workspace-walked portion by basename so the picker
        // starts in a stable order even when the user types nothing.
        if (items.Count > openTabsEnd)
        {
            items.Sort(openTabsEnd, items.Count - openTabsEnd,
                Comparer<PickerItem>.Create((a, b) =>
                    StringComparer.OrdinalIgnoreCase.Compare(a.Display, b.Display)));
        }
        return items;
    }

    private static void WalkAndCollect(string dir, IReadOnlyList<string> excludes, List<PickerItem> items, HashSet<string> seen)
    {
        if (items.Count >= PickerMaxFiles) return;
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(dir); }
        catch { return; }

        foreach (var entry in entries)
        {
            if (items.Count >= PickerMaxFiles) return;
            var name = Path.GetFileName(entry);
            if (string.IsNullOrEmpty(name)) continue;
            if (AlwaysSkipDirs.Contains(name)) continue;
            if (excludes.Contains(name, StringComparer.Ordinal)) continue;

            if (Directory.Exists(entry))
            {
                WalkAndCollect(entry, excludes, items, seen);
                continue;
            }
            if (!seen.Add(entry)) continue;
            items.Add(new PickerItem(PickerKind.File, name, entry, entry, 0, 0));
        }
    }

    private List<PickerItem> CollectPickerSymbols()
    {
        var items = new List<PickerItem>();
        if (!IsToshTab()) return items;
        if (Environment.GetEnvironmentVariable("TOME_NO_LSP") == "1") return items;
        var source = string.IsNullOrEmpty(Current.FilePath) ? "untitled.tosh" : Current.FilePath;
        IReadOnlyList<LspDocumentSymbol> syms;
        try { syms = _features.GetDocumentSymbols(_buffer.GetText(), source); }
        catch { return items; }
        FlattenSymbols(syms, container: null, items);
        return items;
    }

    private static void FlattenSymbols(IReadOnlyList<LspDocumentSymbol> syms, string? container, List<PickerItem> items)
    {
        if (syms is null) return;
        foreach (var s in syms)
        {
            var hint = SymbolKindName(s.Kind) + (container is null ? string.Empty : $"  in {container}");
            items.Add(new PickerItem(
                PickerKind.Symbol,
                s.Name,
                hint,
                string.Empty,
                s.SelectionRange.Start.Line,
                s.SelectionRange.Start.Character));
            if (s.Children is { Count: > 0 })
            {
                var path = container is null ? s.Name : container + "." + s.Name;
                FlattenSymbols(s.Children, path, items);
            }
        }
    }

    private static string SymbolKindName(int kind) => kind switch
    {
        5 => "class",
        6 => "method",
        9 => "ctor",
        11 => "interface",
        12 => "function",
        13 => "var",
        14 => "const",
        22 => "struct",
        10 => "enum",
        21 => "null",
        _ => "sym",
    };

    private void RefilterPicker()
    {
        _pickerFiltered.Clear();
        var symbolMode = _pickerQuery.StartsWith("@", StringComparison.Ordinal);
        var pool = symbolMode ? _pickerSymbols : _pickerFiles;
        var q = symbolMode ? _pickerQuery[1..] : _pickerQuery;

        if (q.Length == 0)
        {
            foreach (var it in pool)
                _pickerFiltered.Add((it, 0));
        }
        else
        {
            foreach (var it in pool)
            {
                var score = FuzzyScore(it.Display, q);
                if (score == int.MinValue) continue;
                _pickerFiltered.Add((it, score));
            }
            // Stable descending sort by score; ties keep original order.
            _pickerFiltered.Sort((a, b) => b.Score.CompareTo(a.Score));
        }

        if (_pickerFiltered.Count == 0) { _pickerSelected = 0; _pickerScroll = 0; return; }
        if (_pickerSelected >= _pickerFiltered.Count) _pickerSelected = _pickerFiltered.Count - 1;
        if (_pickerSelected < 0) _pickerSelected = 0;
        ClampPickerScroll();
    }

    /// <summary>
    /// Simple subsequence fuzzy score: every query char must occur in order
    /// in the candidate (case-insensitive). Bonuses for matches at the
    /// start, after a separator, and for consecutive runs. Returns
    /// <see cref="int.MinValue"/> when the candidate doesn't match.
    /// </summary>
    private static int FuzzyScore(string candidate, string query)
    {
        if (query.Length == 0) return 0;
        var ci = 0; // candidate index
        var score = 0;
        var prevMatched = false;
        for (var qi = 0; qi < query.Length; qi++)
        {
            var qc = char.ToLowerInvariant(query[qi]);
            var found = false;
            while (ci < candidate.Length)
            {
                var cc = char.ToLowerInvariant(candidate[ci]);
                if (cc == qc)
                {
                    var bonus = 1;
                    if (ci == 0) bonus += 8;
                    else
                    {
                        var prev = candidate[ci - 1];
                        if (prev is '/' or '\\' or '.' or '_' or '-') bonus += 6;
                    }
                    if (prevMatched) bonus += 4;
                    score += bonus;
                    ci++;
                    prevMatched = true;
                    found = true;
                    break;
                }
                ci++;
                prevMatched = false;
            }
            if (!found) return int.MinValue;
        }
        // Slight bias against very long candidates so ties prefer shorter names.
        return score - candidate.Length / 16;
    }

    private void ClampPickerScroll()
    {
        if (_pickerSelected < _pickerScroll) _pickerScroll = _pickerSelected;
        else if (_pickerSelected >= _pickerScroll + PickerMaxVisible)
            _pickerScroll = _pickerSelected - PickerMaxVisible + 1;
        if (_pickerScroll < 0) _pickerScroll = 0;
    }

    private bool HandlePickerKey(ConsoleKeyInfo key)
    {
        if (!_pickerOpen) return false;
        switch (key.Key)
        {
            case ConsoleKey.Escape:
                ClosePicker();
                return true;
            case ConsoleKey.Enter:
                AcceptPicker();
                return true;
            case ConsoleKey.UpArrow:
                MovePickerSelection(-1);
                return true;
            case ConsoleKey.DownArrow:
                MovePickerSelection(+1);
                return true;
            case ConsoleKey.PageUp:
                MovePickerSelection(-PickerMaxVisible);
                return true;
            case ConsoleKey.PageDown:
                MovePickerSelection(+PickerMaxVisible);
                return true;
            case ConsoleKey.Home:
                _pickerSelected = 0;
                ClampPickerScroll();
                return true;
            case ConsoleKey.End:
                _pickerSelected = Math.Max(0, _pickerFiltered.Count - 1);
                ClampPickerScroll();
                return true;
            case ConsoleKey.Backspace:
                if (_pickerQuery.Length > 0)
                {
                    _pickerQuery = _pickerQuery[..^1];
                    _pickerSelected = 0;
                    _pickerScroll = 0;
                    RefilterPicker();
                }
                else ClosePicker();
                return true;
        }

        if (!char.IsControl(key.KeyChar))
        {
            _pickerQuery += key.KeyChar;
            _pickerSelected = 0;
            _pickerScroll = 0;
            RefilterPicker();
            return true;
        }
        return true; // swallow other keys while picker is open
    }

    private void MovePickerSelection(int delta)
    {
        if (_pickerFiltered.Count == 0) return;
        _pickerSelected = Math.Clamp(_pickerSelected + delta, 0, _pickerFiltered.Count - 1);
        ClampPickerScroll();
    }

    private void AcceptPicker()
    {
        if (_pickerFiltered.Count == 0) { ClosePicker(); return; }
        var pick = _pickerFiltered[_pickerSelected].Item;
        ClosePicker();
        if (pick.Kind == PickerKind.File)
        {
            OpenPathInTab(pick.Target);
        }
        else
        {
            var line = Math.Max(0, Math.Min(pick.SymbolLine, _buffer.LineCount - 1));
            var lineLen = _buffer.GetLineLength(line);
            var col = Math.Max(0, Math.Min(pick.SymbolCol, lineLen));
            _buffer.ClearSelection();
            _buffer.MoveCursor(new TextLocation(line, col));
            _view.EnsureCursorVisible();
            _message = $"→ {pick.Display}";
        }
    }

    private void OpenPathInTab(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        // If an existing tab already holds the file, switch to it.
        for (var i = 0; i < _tabs.Count; i++)
        {
            if (string.Equals(_tabs[i].FilePath, path, StringComparison.Ordinal))
            {
                _active = i;
                _message = $"→ {path}";
                return;
            }
        }
        string text;
        try { text = File.Exists(path) ? File.ReadAllText(path) : string.Empty; }
        catch (Exception ex) { _message = $"open failed: {ex.Message}"; return; }
        var opened = new Tab(path, text, null);
        opened.Colorizer = ResolveColorizer(opened);
        _tabs.Add(opened);
        _active = _tabs.Count - 1;
        _message = $"opened {path}";
    }

    private void PaintFuzzyPicker(StringBuilder sb, int screenWidth, int screenHeight)
    {
        if (!_pickerOpen) return;

        var visible = Math.Min(PickerMaxVisible, Math.Max(1, _pickerFiltered.Count));
        var height = visible + 4; // top border + title + body + bottom + hint
        var width = Math.Min(80, Math.Max(40, screenWidth - 8));
        var top = Math.Max(1, (screenHeight - height) / 2);
        var left = Math.Max(1, (screenWidth - width) / 2);

        var bg = TomeTheme.Active.Open(Role.PopupBg);
        var sel = TomeTheme.Active.Open(Role.PopupSelectedBg);

        // Top border with title.
        var symbolMode = _pickerQuery.StartsWith("@", StringComparison.Ordinal);
        var modeLabel = symbolMode ? " symbols " : " files ";
        var title = $"{modeLabel}— {_pickerFiltered.Count}/{(_pickerQuery.StartsWith("@") ? _pickerSymbols.Count : _pickerFiles.Count)}";
        sb.Append("\u001b[").Append(top).Append(';').Append(left).Append('H');
        sb.Append(bg).Append(PadOrTrim(' ' + title + ' ', width)).Append("\u001b[0m");

        // Query row.
        sb.Append("\u001b[").Append(top + 1).Append(';').Append(left).Append('H');
        sb.Append(bg);
        var prompt = "  » ";
        var queryLine = prompt + _pickerQuery;
        sb.Append(PadOrTrim(queryLine, width));
        sb.Append("\u001b[0m");

        // Body rows.
        for (var i = 0; i < visible; i++)
        {
            var row = top + 2 + i;
            sb.Append("\u001b[").Append(row).Append(';').Append(left).Append('H');
            var idx = _pickerScroll + i;
            if (idx >= _pickerFiltered.Count)
            {
                sb.Append(bg).Append(new string(' ', width)).Append("\u001b[0m");
                continue;
            }
            var item = _pickerFiltered[idx].Item;
            var selected = idx == _pickerSelected;
            sb.Append(selected ? sel : bg);

            var marker = selected ? "› " : "  ";
            var displayMax = Math.Min(item.Display.Length, width / 2);
            var display = item.Display.Length <= displayMax ? item.Display : item.Display[..(displayMax - 1)] + "…";

            var line = new StringBuilder();
            line.Append(' ').Append(marker).Append(display);
            if (!string.IsNullOrEmpty(item.Hint))
            {
                line.Append("   ");
                var hintMax = Math.Max(0, width - line.Length - 2);
                var hint = item.Hint;
                if (hint.Length > hintMax)
                    hint = hintMax <= 1 ? hint[..hintMax] : "…" + hint[^(hintMax - 1)..];
                line.Append(hint);
            }
            sb.Append(PadOrTrim(line.ToString(), width));
            sb.Append("\u001b[0m");
        }

        // Hint row.
        sb.Append("\u001b[").Append(top + 2 + visible).Append(';').Append(left).Append('H');
        sb.Append(bg);
        var hintText = "  Enter open · Esc cancel · @… symbols ";
        sb.Append(PadOrTrim(hintText, width));
        sb.Append("\u001b[0m");
    }

    private (int Row, int Col) GetPickerCursorScreenPosition(int screenWidth, int screenHeight)
    {
        var visible = Math.Min(PickerMaxVisible, Math.Max(1, _pickerFiltered.Count));
        var height = visible + 4;
        var width = Math.Min(80, Math.Max(40, screenWidth - 8));
        var top = Math.Max(1, (screenHeight - height) / 2);
        var left = Math.Max(1, (screenWidth - width) / 2);
        // Query row is top+1; column = left + len("  » ") + query.Length, clamped.
        var col = Math.Min(left + width - 1, left + 4 + _pickerQuery.Length);
        return (top + 1, col);
    }

    private static string PadOrTrim(string s, int width)
    {
        if (s.Length == width) return s;
        if (s.Length < width) return s + new string(' ', width - s.Length);
        return s[..width];
    }
}
