using Tosh.Tui.Editing;

namespace Tosh.Tome;

/// <summary>
/// Vim-style motions, operators, and text objects for Command mode.
///
/// <para>
/// Single-character motions (<c>h j k l 0 $ w b G</c>, etc.) are dispatched
/// from <c>HandleCommandModeKey</c> in <c>ModalCommand.cs</c>. This file
/// adds the multi-key sequences:
/// </para>
///
/// <list type="bullet">
///   <item><b>Operators</b> — <c>d</c> (delete), <c>c</c> (change), <c>y</c>
///         (yank). Each is composed with a motion or a text object.</item>
///   <item><b>Two-key motions</b> — <c>gg</c> (top), <c>f&lt;c&gt;</c>,
///         <c>F&lt;c&gt;</c>, <c>t&lt;c&gt;</c>, <c>T&lt;c&gt;</c>.</item>
///   <item><b>Text objects</b> — <c>iw aw i" a" i' a' i( a( i{ a{ i[ a[
///         i&lt; a&lt; i` a` ip ap is as</c>. <c>is</c>/<c>as</c> use the
///         active tree-sitter parse when available.</item>
///   <item><b>Repeats</b> — <c>;</c> / <c>,</c> repeat the last
///         <c>f</c>/<c>F</c>/<c>t</c>/<c>T</c>.</item>
/// </list>
/// </summary>
internal sealed partial class TomeApp
{
    // ─── Pending command state ───────────────────────────────────────────

    private enum Operator { None, Delete, Change, Yank }

    private Operator _pendingOp = Operator.None;
    private char? _pendingPrefix; // 'g', 'f', 'F', 't', 'T', 'i', 'a'

    // Last-used f/F/t/T target, for ';' and ',' repeats.
    private char? _lastFindChar;
    private char _lastFindKind; // 'f' 'F' 't' 'T'

    // ─── Dispatcher entry ────────────────────────────────────────────────

    /// <summary>
    /// Routes a Command-mode keypress through the pending-state machine.
    /// Returns <c>true</c> when the key was consumed by an operator or
    /// multi-key sequence so the caller skips its single-key table.
    /// </summary>
    private bool TryHandleMotionOrOperator(ConsoleKeyInfo key)
    {
        var ch = key.KeyChar;

        // While a text-object selector is pending ('i' or 'a' after an
        // operator), the next key is the object kind.
        if (_pendingPrefix is 'i' or 'a' && _pendingOp != Operator.None)
        {
            var inside = _pendingPrefix == 'i';
            ClearPending();
            ApplyTextObject(_pendingOp, inside, ch);
            return true;
        }

        // While an f/F/t/T target is pending, the next key is the literal.
        if (_pendingPrefix is 'f' or 'F' or 't' or 'T')
        {
            var kind = _pendingPrefix.Value;
            ClearPending();
            if (ch == (char)27 || key.Key == ConsoleKey.Escape) return true;
            DoFindChar(kind, ch, recordRepeat: true);
            return true;
        }

        // 'gg' — top of file.
        if (_pendingPrefix == 'g')
        {
            ClearPending();
            if (ch == 'g') { _buffer.MoveCursor(new TextLocation(0, 0)); return true; }
            return true; // swallow whatever followed the lone 'g'
        }

        // While an operator is pending we expect a motion or text-object
        // selector. A second copy of the same operator is linewise
        // (dd / cc / yy).
        if (_pendingOp != Operator.None)
        {
            // Linewise (dd, cc, yy).
            if ((ch == 'd' && _pendingOp == Operator.Delete)
                || (ch == 'c' && _pendingOp == Operator.Change)
                || (ch == 'y' && _pendingOp == Operator.Yank))
            {
                var op = _pendingOp;
                ClearPending();
                ApplyLinewise(op);
                return true;
            }

            if (ch is 'i' or 'a')
            {
                _pendingPrefix = ch;
                return true;
            }

            // Motion-as-range: any motion that produces a destination is
            // turned into a [start, end) range and handed to the operator.
            if (TryMotionRange(key, out var start, out var end))
            {
                var op = _pendingOp;
                ClearPending();
                ApplyOperatorRange(op, start, end);
                return true;
            }

            // Anything else cancels.
            ClearPending();
            return true;
        }

        // No pending state — recognise opens.
        switch (ch)
        {
            case 'd': _pendingOp = Operator.Delete; return true;
            case 'c': _pendingOp = Operator.Change; return true;
            case 'y': _pendingOp = Operator.Yank; return true;
            case 'g': _pendingPrefix = 'g'; return true;
            case 'f': _pendingPrefix = 'f'; return true;
            case 'F': _pendingPrefix = 'F'; return true;
            case 't': _pendingPrefix = 't'; return true;
            case 'T': _pendingPrefix = 'T'; return true;
            case ';':
                if (_lastFindChar is { } c1) DoFindChar(_lastFindKind, c1, recordRepeat: false);
                return true;
            case ',':
                if (_lastFindChar is { } c2)
                {
                    var inv = _lastFindKind switch { 'f' => 'F', 'F' => 'f', 't' => 'T', 'T' => 't', _ => _lastFindKind };
                    DoFindChar(inv, c2, recordRepeat: false);
                }
                return true;
            case 'e':
                _buffer.MoveCursor(EndOfWordFrom(_buffer.Cursor));
                return true;
            case '{':
                _buffer.MoveCursor(PrevParagraph(_buffer.Cursor));
                return true;
            case '}':
                _buffer.MoveCursor(NextParagraph(_buffer.Cursor));
                return true;
            case '%':
                if (FindMatchingBracket(_buffer.Cursor) is { } match) _buffer.MoveCursor(match);
                return true;
            case 'H':
                _buffer.MoveCursor(new TextLocation(_view.ScrollLine, 0));
                return true;
            case 'L':
                _buffer.MoveCursor(new TextLocation(
                    Math.Min(_buffer.LineCount - 1, _view.ScrollLine + _view.ViewportHeight - 1), 0));
                return true;
            case 'M':
                _buffer.MoveCursor(new TextLocation(
                    Math.Min(_buffer.LineCount - 1, _view.ScrollLine + _view.ViewportHeight / 2), 0));
                return true;
            case '*': SearchWordUnderCursor(forward: true); return true;
            case '#': SearchWordUnderCursor(forward: false); return true;
            case 'D': // delete to EOL
                ApplyOperatorRange(Operator.Delete, _buffer.Cursor,
                    new TextLocation(_buffer.Cursor.Line, _buffer.GetLineLength(_buffer.Cursor.Line)));
                return true;
            case 'C': // change to EOL
                ApplyOperatorRange(Operator.Change, _buffer.Cursor,
                    new TextLocation(_buffer.Cursor.Line, _buffer.GetLineLength(_buffer.Cursor.Line)));
                return true;
            case 'Y': // yank line
                ApplyLinewise(Operator.Yank);
                return true;
            case 'p': PasteAfter(); return true;
            case 'P': PasteBefore(); return true;
        }

        // Ctrl+D / Ctrl+U — half-page scroll.
        if ((key.Modifiers & ConsoleModifiers.Control) != 0)
        {
            if (key.Key == ConsoleKey.D) { PageBy(_view.ViewportHeight / 2); return true; }
            if (key.Key == ConsoleKey.U) { PageBy(-_view.ViewportHeight / 2); return true; }
        }

        return false;
    }

    private void ClearPending()
    {
        _pendingOp = Operator.None;
        _pendingPrefix = null;
    }

    // ─── Motion → range helper ───────────────────────────────────────────

    /// <summary>
    /// Resolves a single motion key into a [start, end) range starting
    /// at the current cursor. Returns false for keys that are not
    /// motions in their own right.
    /// </summary>
    private bool TryMotionRange(ConsoleKeyInfo key, out TextLocation start, out TextLocation end)
    {
        start = _buffer.Cursor;
        end = _buffer.Cursor;
        var ch = key.KeyChar;
        switch (ch)
        {
            case 'h': end = OneLeft(start); break;
            case 'l': end = OneRight(start); break;
            case 'w': end = WordRightFrom(start); break;
            case 'b': end = WordLeftFrom(start); break;
            case 'e': end = EndOfWordFrom(start); end = OneRight(end); break;
            case '0': end = start with { Column = 0 }; break;
            case '$': end = start with { Column = _buffer.GetLineLength(start.Line) }; break;
            case '{': end = PrevParagraph(start); break;
            case '}': end = NextParagraph(start); break;
            case 'G': end = new TextLocation(_buffer.LineCount - 1, 0); break;
            case '%':
                if (FindMatchingBracket(start) is { } m) { end = OneRight(m); break; }
                return false;
            default: return false;
        }
        Normalize(ref start, ref end);
        return true;
    }

    private static void Normalize(ref TextLocation a, ref TextLocation b)
    {
        if (CompareLoc(a, b) > 0) (a, b) = (b, a);
    }

    private static int CompareLoc(TextLocation a, TextLocation b)
    {
        if (a.Line != b.Line) return a.Line.CompareTo(b.Line);
        return a.Column.CompareTo(b.Column);
    }

    // ─── Operator application ────────────────────────────────────────────

    private void ApplyOperatorRange(Operator op, TextLocation start, TextLocation end)
    {
        Normalize(ref start, ref end);
        if (CompareLoc(start, end) == 0) { _message = "empty range"; return; }
        _buffer.MoveCursor(start);
        _buffer.BeginSelection();
        _buffer.MoveCursor(end);

        switch (op)
        {
            case Operator.Yank:
                var text = _buffer.GetSelectionText();
                Clipboard.SetText(text);
                _buffer.ClearSelection();
                _buffer.MoveCursor(start);
                _message = $"yanked {text.Length} char(s)";
                break;
            case Operator.Delete:
                var deleted = _buffer.DeleteSelection();
                Clipboard.SetText(deleted);
                _message = $"deleted {deleted.Length} char(s)";
                break;
            case Operator.Change:
                var changed = _buffer.DeleteSelection();
                Clipboard.SetText(changed);
                EnterEditMode();
                break;
        }
    }

    private void ApplyLinewise(Operator op)
    {
        var line = _buffer.Cursor.Line;
        var lineCount = _buffer.LineCount;
        var start = new TextLocation(line, 0);
        var end = line + 1 < lineCount
            ? new TextLocation(line + 1, 0)
            : new TextLocation(line, _buffer.GetLineLength(line));
        ApplyOperatorRange(op, start, end);
    }

    private void PasteAfter()
    {
        var clip = Clipboard.GetText();
        if (string.IsNullOrEmpty(clip)) { _message = "clipboard empty"; return; }
        // Move past current char if mid-line, then insert.
        if (_buffer.Cursor.Column < _buffer.GetLineLength(_buffer.Cursor.Line))
            _buffer.MoveRight();
        _buffer.InsertText(clip);
        _message = $"pasted {clip.Length} char(s)";
    }

    private void PasteBefore()
    {
        var clip = Clipboard.GetText();
        if (string.IsNullOrEmpty(clip)) { _message = "clipboard empty"; return; }
        _buffer.InsertText(clip);
        _message = $"pasted {clip.Length} char(s)";
    }

    // ─── Char-find (f / F / t / T) ───────────────────────────────────────

    private void DoFindChar(char kind, char target, bool recordRepeat)
    {
        var cursor = _buffer.Cursor;
        var line = _buffer.GetLine(cursor.Line);
        int idx;
        switch (kind)
        {
            case 'f':
                idx = line.IndexOf(target, Math.Min(cursor.Column + 1, line.Length));
                if (idx >= 0) _buffer.MoveCursor(new TextLocation(cursor.Line, idx));
                else _message = $"no '{target}' on line";
                break;
            case 'F':
                idx = line.LastIndexOf(target, Math.Max(0, cursor.Column - 1));
                if (idx >= 0) _buffer.MoveCursor(new TextLocation(cursor.Line, idx));
                else _message = $"no '{target}' on line";
                break;
            case 't':
                idx = line.IndexOf(target, Math.Min(cursor.Column + 1, line.Length));
                if (idx > 0) _buffer.MoveCursor(new TextLocation(cursor.Line, idx - 1));
                else _message = $"no '{target}' on line";
                break;
            case 'T':
                idx = line.LastIndexOf(target, Math.Max(0, cursor.Column - 1));
                if (idx >= 0 && idx + 1 < line.Length)
                    _buffer.MoveCursor(new TextLocation(cursor.Line, idx + 1));
                else _message = $"no '{target}' on line";
                break;
        }
        if (recordRepeat) { _lastFindChar = target; _lastFindKind = kind; }
    }

    // ─── Text objects ────────────────────────────────────────────────────

    private void ApplyTextObject(Operator op, bool inside, char kind)
    {
        (TextLocation, TextLocation)? range = kind switch
        {
            'w' => WordObject(inside),
            '"' => QuoteObject(inside, '"'),
            '\'' => QuoteObject(inside, '\''),
            '`' => QuoteObject(inside, '`'),
            '(' or ')' or 'b' => PairObject(inside, '(', ')'),
            '[' or ']' => PairObject(inside, '[', ']'),
            '{' or '}' or 'B' => PairObject(inside, '{', '}'),
            '<' or '>' => PairObject(inside, '<', '>'),
            'p' => ParagraphObject(inside),
            's' => SyntaxNodeObject(inside),
            _ => null,
        };
        if (range is null) { _message = $"no {(inside ? "inner" : "around")} '{kind}' under cursor"; return; }
        var (a, b) = range.Value;
        ApplyOperatorRange(op, a, b);
    }

    private (TextLocation, TextLocation)? WordObject(bool inside)
    {
        var cur = _buffer.Cursor;
        var line = _buffer.GetLine(cur.Line);
        if (line.Length == 0) return null;
        var col = Math.Min(cur.Column, line.Length - 1);
        if (col < 0) return null;

        var l = col;
        while (l > 0 && !IsWordSep(line[l - 1])) l--;
        var r = col;
        while (r < line.Length && !IsWordSep(line[r])) r++;
        if (l == r) return null; // not on a word

        if (!inside)
        {
            // 'aw' includes one side of trailing whitespace.
            while (r < line.Length && line[r] == ' ') r++;
        }
        return (new TextLocation(cur.Line, l), new TextLocation(cur.Line, r));
    }

    private static bool IsWordSep(char c) => !char.IsLetterOrDigit(c) && c != '_';

    private (TextLocation, TextLocation)? QuoteObject(bool inside, char q)
    {
        var cur = _buffer.Cursor;
        var line = _buffer.GetLine(cur.Line);
        // Find quote pair on the current line surrounding the cursor.
        var left = -1;
        for (var i = Math.Min(cur.Column, line.Length - 1); i >= 0; i--)
            if (line[i] == q) { left = i; break; }
        if (left < 0) return null;
        var right = line.IndexOf(q, left + 1);
        if (right < 0) return null;

        if (inside) return (new TextLocation(cur.Line, left + 1), new TextLocation(cur.Line, right));
        return (new TextLocation(cur.Line, left), new TextLocation(cur.Line, right + 1));
    }

    private (TextLocation, TextLocation)? PairObject(bool inside, char open, char close)
    {
        var cur = _buffer.Cursor;
        // Search backwards for the opening bracket (across lines).
        if (FindEnclosing(cur, open, close) is not (TextLocation o, TextLocation c)) return null;
        if (inside) return (OneRight(o), c);
        return (o, OneRight(c));
    }

    private (TextLocation, TextLocation)? ParagraphObject(bool inside)
    {
        var cur = _buffer.Cursor;
        var top = cur.Line;
        while (top > 0 && _buffer.GetLine(top - 1).Length > 0) top--;
        var bot = cur.Line;
        while (bot + 1 < _buffer.LineCount && _buffer.GetLine(bot + 1).Length > 0) bot++;

        var startLoc = new TextLocation(top, 0);
        var endLine = bot;
        if (!inside)
        {
            // include one trailing blank line
            if (endLine + 1 < _buffer.LineCount && _buffer.GetLine(endLine + 1).Length == 0) endLine++;
        }
        var endLoc = endLine + 1 < _buffer.LineCount
            ? new TextLocation(endLine + 1, 0)
            : new TextLocation(endLine, _buffer.GetLineLength(endLine));
        return (startLoc, endLoc);
    }

    /// <summary>
    /// <c>is</c>/<c>as</c> — the innermost tree-sitter syntax node
    /// containing the cursor. Falls back to <see cref="WordObject"/>
    /// when no tree-sitter colorizer is attached to this tab.
    /// </summary>
    private (TextLocation, TextLocation)? SyntaxNodeObject(bool inside)
    {
        if (Current.Colorizer is TreeSitter.TreeSitterColorizer ts)
        {
            if (ts.RangeAt(_buffer.Cursor) is { } range)
            {
                // 'as' includes one trailing whitespace char on the line.
                if (!inside)
                {
                    var line = _buffer.GetLine(range.end.Line);
                    if (range.end.Column < line.Length && line[range.end.Column] == ' ')
                        range = (range.start, range.end with { Column = range.end.Column + 1 });
                }
                return (range.start, range.end);
            }
        }
        return WordObject(inside);
    }

    // ─── Geometric helpers ───────────────────────────────────────────────

    private TextLocation OneLeft(TextLocation loc)
    {
        if (loc.Column > 0) return loc with { Column = loc.Column - 1 };
        if (loc.Line > 0) return new TextLocation(loc.Line - 1, _buffer.GetLineLength(loc.Line - 1));
        return loc;
    }

    private TextLocation OneRight(TextLocation loc)
    {
        var len = _buffer.GetLineLength(loc.Line);
        if (loc.Column < len) return loc with { Column = loc.Column + 1 };
        if (loc.Line + 1 < _buffer.LineCount) return new TextLocation(loc.Line + 1, 0);
        return loc;
    }

    private TextLocation WordLeftFrom(TextLocation loc)
    {
        var saved = _buffer.Cursor;
        _buffer.MoveCursor(loc);
        _buffer.MoveWordLeft();
        var r = _buffer.Cursor;
        _buffer.MoveCursor(saved);
        return r;
    }

    private TextLocation WordRightFrom(TextLocation loc)
    {
        var saved = _buffer.Cursor;
        _buffer.MoveCursor(loc);
        _buffer.MoveWordRight();
        var r = _buffer.Cursor;
        _buffer.MoveCursor(saved);
        return r;
    }

    private TextLocation EndOfWordFrom(TextLocation loc)
    {
        var line = _buffer.GetLine(loc.Line);
        var col = loc.Column;
        if (col >= line.Length)
        {
            if (loc.Line + 1 >= _buffer.LineCount) return loc;
            return EndOfWordFrom(new TextLocation(loc.Line + 1, 0));
        }
        // Skip separators first if we're not in a word.
        while (col < line.Length && IsWordSep(line[col])) col++;
        // Then scan to the end of the current word.
        while (col + 1 < line.Length && !IsWordSep(line[col + 1])) col++;
        return new TextLocation(loc.Line, col);
    }

    private TextLocation PrevParagraph(TextLocation loc)
    {
        var line = loc.Line - 1;
        // Skip blank lines we might be sitting in.
        while (line > 0 && _buffer.GetLine(line).Length == 0) line--;
        // Walk up to the first blank line above.
        while (line > 0 && _buffer.GetLine(line).Length > 0) line--;
        return new TextLocation(Math.Max(0, line), 0);
    }

    private TextLocation NextParagraph(TextLocation loc)
    {
        var line = loc.Line + 1;
        while (line < _buffer.LineCount && _buffer.GetLine(line).Length == 0) line++;
        while (line < _buffer.LineCount && _buffer.GetLine(line).Length > 0) line++;
        return new TextLocation(Math.Min(_buffer.LineCount - 1, line), 0);
    }

    /// <summary>
    /// Locate the matching bracket for the one at <paramref name="loc"/>.
    /// </summary>
    private TextLocation? FindMatchingBracket(TextLocation loc)
    {
        var line = _buffer.GetLine(loc.Line);
        if (loc.Column >= line.Length) return null;
        var ch = line[loc.Column];
        var (open, close, forward) = ch switch
        {
            '(' => ('(', ')', true),
            ')' => ('(', ')', false),
            '[' => ('[', ']', true),
            ']' => ('[', ']', false),
            '{' => ('{', '}', true),
            '}' => ('{', '}', false),
            '<' => ('<', '>', true),
            '>' => ('<', '>', false),
            _ => ((char)0, (char)0, true),
        };
        if (open == 0) return null;
        return forward
            ? ScanForward(loc, open, close)
            : ScanBackward(loc, open, close);
    }

    private TextLocation? ScanForward(TextLocation start, char open, char close)
    {
        var depth = 0;
        var li = start.Line;
        var col = start.Column;
        while (li < _buffer.LineCount)
        {
            var line = _buffer.GetLine(li);
            while (col < line.Length)
            {
                var c = line[col];
                if (c == open) depth++;
                else if (c == close) { depth--; if (depth == 0) return new TextLocation(li, col); }
                col++;
            }
            li++; col = 0;
        }
        return null;
    }

    private TextLocation? ScanBackward(TextLocation start, char open, char close)
    {
        var depth = 0;
        var li = start.Line;
        var col = start.Column;
        while (li >= 0)
        {
            var line = _buffer.GetLine(li);
            while (col >= 0 && col < line.Length + 1)
            {
                if (col < line.Length)
                {
                    var c = line[col];
                    if (c == close) depth++;
                    else if (c == open) { depth--; if (depth == 0) return new TextLocation(li, col); }
                }
                col--;
            }
            li--;
            if (li >= 0) col = _buffer.GetLineLength(li) - 1;
        }
        return null;
    }

    private (TextLocation, TextLocation)? FindEnclosing(TextLocation loc, char open, char close)
    {
        // Walk backward counting unmatched closes to find the enclosing open.
        var depth = 0;
        TextLocation? oLoc = null;
        var li = loc.Line;
        var col = Math.Min(loc.Column, _buffer.GetLineLength(li));
        while (li >= 0 && oLoc is null)
        {
            var line = _buffer.GetLine(li);
            for (var c = Math.Min(col, line.Length - 1); c >= 0; c--)
            {
                var ch = line[c];
                if (ch == close && (li != loc.Line || c != loc.Column)) depth++;
                else if (ch == open)
                {
                    if (depth == 0) { oLoc = new TextLocation(li, c); break; }
                    depth--;
                }
            }
            li--;
            if (li >= 0) col = _buffer.GetLineLength(li);
        }
        if (oLoc is null) return null;
        var match = ScanForward(oLoc.Value, open, close);
        if (match is null) return null;
        return (oLoc.Value, match.Value);
    }

    // ─── '*' / '#' — search word under cursor ────────────────────────────

    private void SearchWordUnderCursor(bool forward)
    {
        var line = _buffer.GetLine(_buffer.Cursor.Line);
        var col = _buffer.Cursor.Column;
        if (col >= line.Length || IsWordSep(line[col])) { _message = "no word under cursor"; return; }
        var l = col;
        while (l > 0 && !IsWordSep(line[l - 1])) l--;
        var r = col;
        while (r < line.Length && !IsWordSep(line[r])) r++;
        var word = line[l..r];
        Current.LastSearch = word;
        if (forward) FindFrom(_buffer.Cursor, word, includeCurrent: false);
        else FindBackward(_buffer.Cursor, word);
    }

    private void FindBackward(TextLocation start, string needle)
    {
        var cmp = Current.SearchIgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        for (var pass = 0; pass < 2; pass++)
        {
            var firstLine = pass == 0 ? start.Line : _buffer.LineCount - 1;
            var lastLine = pass == 0 ? 0 : start.Line + 1;
            for (var i = firstLine; i >= lastLine && i >= 0; i--)
            {
                var line = _buffer.GetLine(i);
                var maxCol = (pass == 0 && i == start.Line)
                    ? Math.Min(start.Column - 1, line.Length)
                    : line.Length;
                if (maxCol < 0) continue;
                var idx = line.LastIndexOf(needle, maxCol, cmp);
                if (idx >= 0)
                {
                    _buffer.MoveCursor(new TextLocation(i, idx));
                    _message = pass == 1 ? $"wrapped to match at {i + 1}:{idx + 1}" : $"match at {i + 1}:{idx + 1}";
                    return;
                }
            }
        }
        _message = $"not found: {needle}";
    }
}
