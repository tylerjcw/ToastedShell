using System.Text;
using Tosh.Language;
using Tosh.Runtime;
using Tosh.Tome.Theme;

namespace Tosh.Tome;

/// <summary>
/// Embedded Tōsh REPL hosted in a horizontal split at the bottom of the
/// editor pane. Owns its own <see cref="ToshEngine"/> + <see cref="ToshRuntime"/>;
/// commands execute synchronously (the editor is frozen for the duration),
/// with output captured by temporarily redirecting <see cref="Console.Out"/>
/// and <see cref="Console.Error"/>.
/// </summary>
internal sealed class ReplPane
{
    private const int MinHeight = 4;
    private const int DefaultHeight = 10;
    private const int MaxTranscriptLines = 5_000;

    private readonly List<string> _transcript = new();
    private readonly List<string> _history = new();
    private readonly StringBuilder _input = new();
    private int _cursor;
    private int _historyIndex; // points past end when not navigating
    private int _scroll;       // logical lines scrolled up from bottom

    private ToshEngine? _engine;
    private string? _cwd;
    private bool _busy;

    public bool Visible { get; private set; }

    /// <summary>Requested height (rows) of the REPL pane, separator + transcript + input.</summary>
    public int Height { get; set; } = DefaultHeight;

    public bool IsBusy => _busy;

    public void Toggle(string? cwd)
    {
        if (Visible) Close();
        else Open(cwd);
    }

    public void Open(string? cwd)
    {
        if (!Visible)
        {
            Visible = true;
            _cwd = cwd;
            if (_engine is null)
            {
                try
                {
                    _engine = new ToshEngine();
                    if (!string.IsNullOrEmpty(cwd) && Directory.Exists(cwd))
                        _engine.Runtime.CurrentDirectory = cwd;
                    _transcript.Add($"\u001b[2mTōsh embedded REPL — Esc to leave, Ctrl+L to clear, :repl close to hide\u001b[22m");
                }
                catch (Exception ex)
                {
                    _transcript.Add($"\u001b[31mREPL init failed: {ex.Message}\u001b[39m");
                }
            }
        }
    }

    public void Close()
    {
        Visible = false;
    }

    /// <summary>Effective height clamped to a reasonable share of the available editor rows.</summary>
    public int EffectiveHeight(int editorRows)
    {
        if (!Visible) return 0;
        var h = Math.Clamp(Height, MinHeight, Math.Max(MinHeight, editorRows / 2 + 1));
        return Math.Min(h, editorRows - 1);
    }

    /// <summary>
    /// Render the REPL pane into <paramref name="sb"/>, starting at 1-based
    /// terminal row <paramref name="topRow"/>, spanning <paramref name="width"/>
    /// columns starting at 1-based <paramref name="leftCol"/>, for the given
    /// height. Caller is responsible for the cursor position.
    /// </summary>
    public void Render(StringBuilder sb, int topRow, int leftCol, int width, int height, bool focused)
    {
        if (height < 2) return;

        // Title / separator row.
        sb.Append("\u001b[").Append(topRow).Append(';').Append(leftCol).Append('H');
        var title = focused ? " REPL " : " repl ";
        var dashes = Math.Max(0, width - title.Length);
        var left = dashes / 2;
        var right = dashes - left;
        sb.Append("\u001b[2m");
        sb.Append(new string('─', left));
        sb.Append("\u001b[22m");
        if (focused) sb.Append(TomeTheme.Active.Open(Role.StatusBarBg));
        else sb.Append("\u001b[2m");
        sb.Append(title);
        sb.Append("\u001b[0m");
        sb.Append("\u001b[2m");
        sb.Append(new string('─', right));
        sb.Append("\u001b[22m");

        // Wrapped transcript view: render bottom-up.
        var bodyRows = height - 2; // title + input
        var wrapped = WrapAll(width);
        var total = wrapped.Count;
        var maxScroll = Math.Max(0, total - bodyRows);
        if (_scroll > maxScroll) _scroll = maxScroll;
        var firstShown = Math.Max(0, total - bodyRows - _scroll);
        for (var i = 0; i < bodyRows; i++)
        {
            sb.Append("\u001b[").Append(topRow + 1 + i).Append(';').Append(leftCol).Append('H');
            sb.Append("\u001b[2K");
            var srcIdx = firstShown + i;
            if (srcIdx < total) sb.Append(wrapped[srcIdx]);
        }

        // Input row.
        sb.Append("\u001b[").Append(topRow + height - 1).Append(';').Append(leftCol).Append('H');
        sb.Append("\u001b[2K");
        var prompt = _busy ? "… " : "» ";
        sb.Append("\u001b[2m").Append(prompt).Append("\u001b[22m");
        var input = _input.ToString();
        var avail = Math.Max(1, width - prompt.Length);
        // Horizontal scroll so the cursor stays in view.
        var hScroll = 0;
        if (_cursor > avail - 1) hScroll = _cursor - (avail - 1);
        var slice = input.Length > hScroll ? input[hScroll..] : string.Empty;
        if (slice.Length > avail) slice = slice[..avail];
        sb.Append(slice);
    }

    public (int Row, int Col) GetCursorScreenPosition(int topRow, int leftCol, int width, int height)
    {
        // Coordinates are zero-based here; Render() still receives one-based
        // ANSI coordinates because it writes escape sequences directly.
        var prompt = _busy ? "… " : "» ";
        var avail = Math.Max(1, width - prompt.Length);
        var hScroll = _cursor > avail - 1 ? _cursor - (avail - 1) : 0;
        var col = leftCol + prompt.Length + (_cursor - hScroll);
        return (topRow + height - 1, col);
    }

    /// <summary>Returns true if the key was consumed; false means focus should return to editor.</summary>
    public bool HandleKey(ConsoleKeyInfo key)
    {
        if (_busy) return true; // Should never happen — exec is synchronous.

        var ctrl = (key.Modifiers & ConsoleModifiers.Control) != 0;

        if (key.Key == ConsoleKey.Escape) return false; // caller transfers focus.

        if (ctrl && key.Key == ConsoleKey.L)
        {
            _transcript.Clear();
            _scroll = 0;
            return true;
        }

        if (ctrl && key.Key == ConsoleKey.C)
        {
            _input.Clear();
            _cursor = 0;
            _historyIndex = _history.Count;
            return true;
        }

        switch (key.Key)
        {
            case ConsoleKey.Enter:
                ExecuteCurrent();
                return true;
            case ConsoleKey.Backspace:
                if (_cursor > 0) { _input.Remove(_cursor - 1, 1); _cursor--; }
                return true;
            case ConsoleKey.Delete:
                if (_cursor < _input.Length) _input.Remove(_cursor, 1);
                return true;
            case ConsoleKey.LeftArrow:
                if (_cursor > 0) _cursor--;
                return true;
            case ConsoleKey.RightArrow:
                if (_cursor < _input.Length) _cursor++;
                return true;
            case ConsoleKey.Home:
                _cursor = 0;
                return true;
            case ConsoleKey.End:
                _cursor = _input.Length;
                return true;
            case ConsoleKey.UpArrow:
                NavigateHistory(-1);
                return true;
            case ConsoleKey.DownArrow:
                NavigateHistory(+1);
                return true;
            case ConsoleKey.PageUp:
                _scroll += 4;
                return true;
            case ConsoleKey.PageDown:
                _scroll = Math.Max(0, _scroll - 4);
                return true;
        }

        if (key.KeyChar >= ' ' && !char.IsControl(key.KeyChar))
        {
            _input.Insert(_cursor, key.KeyChar);
            _cursor++;
            return true;
        }
        return true;
    }

    private void NavigateHistory(int delta)
    {
        if (_history.Count == 0) return;
        var idx = _historyIndex + delta;
        idx = Math.Clamp(idx, 0, _history.Count);
        _historyIndex = idx;
        if (idx == _history.Count)
        {
            _input.Clear();
            _cursor = 0;
        }
        else
        {
            _input.Clear();
            _input.Append(_history[idx]);
            _cursor = _input.Length;
        }
    }

    private void ExecuteCurrent()
    {
        var text = _input.ToString();
        var trimmed = text.Trim();
        _input.Clear();
        _cursor = 0;
        _scroll = 0;

        AppendTranscript($"\u001b[2m»\u001b[22m {text}");

        if (trimmed.Length == 0) return;

        if (_history.Count == 0 || !string.Equals(_history[^1], trimmed, StringComparison.Ordinal))
            _history.Add(trimmed);
        _historyIndex = _history.Count;

        if (_engine is null)
        {
            AppendTranscript("\u001b[31mengine unavailable\u001b[39m");
            return;
        }

        _busy = true;
        var prevOut = Console.Out;
        var prevErr = Console.Error;
        var captured = new StringWriter();
        var capturedErr = new StringWriter();
        try
        {
            Console.SetOut(captured);
            Console.SetError(capturedErr);
            _ = _engine.ExecuteToListAsync(text, "<tome-repl>").GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            AppendTranscript($"\u001b[31m{ex.Message}\u001b[39m");
        }
        finally
        {
            Console.SetOut(prevOut);
            Console.SetError(prevErr);
            _busy = false;
        }

        var outText = captured.ToString();
        if (outText.Length > 0)
            foreach (var ln in SplitLines(outText)) AppendTranscript(ln);
        var errText = capturedErr.ToString();
        if (errText.Length > 0)
            foreach (var ln in SplitLines(errText)) AppendTranscript("\u001b[31m" + ln + "\u001b[39m");
    }

    private void AppendTranscript(string line)
    {
        _transcript.Add(line);
        if (_transcript.Count > MaxTranscriptLines)
            _transcript.RemoveRange(0, _transcript.Count - MaxTranscriptLines);
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        // Preserve trailing newlines as empty lines? No — strip a single trailing newline.
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (normalized.EndsWith('\n')) normalized = normalized[..^1];
        return normalized.Split('\n');
    }

    private List<string> WrapAll(int width)
    {
        // For terminal-emitted strings we cannot easily compute display width
        // (ANSI sequences). Keep it pragmatic: wrap on raw characters and let
        // the user widen the pane if escapes mangle the count.
        var result = new List<string>(_transcript.Count);
        foreach (var line in _transcript)
        {
            if (StripAnsiLength(line) <= width) { result.Add(line); continue; }
            // Simple hard wrap on raw chars; ANSI runs may be split but the
            // renderer in modern terminals is forgiving.
            var i = 0;
            while (i < line.Length)
            {
                var take = Math.Min(width, line.Length - i);
                result.Add(line.Substring(i, take));
                i += take;
            }
        }
        return result;
    }

    private static int StripAnsiLength(string s)
    {
        var n = 0;
        var i = 0;
        while (i < s.Length)
        {
            if (s[i] == '\u001b' && i + 1 < s.Length && s[i + 1] == '[')
            {
                i += 2;
                while (i < s.Length && !(s[i] >= 0x40 && s[i] <= 0x7E)) i++;
                if (i < s.Length) i++;
                continue;
            }
            n++;
            i++;
        }
        return n;
    }
}
