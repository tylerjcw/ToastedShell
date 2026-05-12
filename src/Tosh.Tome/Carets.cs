using System.Text.RegularExpressions;
using Tosh.Tui.Editing;

namespace Tosh.Tome;

/// <summary>
/// <c>:carets</c> palette verb. Acts as the bulk-seed interface for the
/// multi-cursor editing model. Single-key bindings (Ctrl+Alt+↑/↓, Alt+click,
/// Esc to collapse) live in <see cref="TomeApp"/>.
/// </summary>
internal sealed partial class TomeApp
{
    /// <summary>
    /// Dispatch <c>:carets &lt;sub&gt;</c>. Subcommands:
    /// <list type="bullet">
    ///   <item><c>:carets</c> or <c>:carets count</c> — print active count</item>
    ///   <item><c>:carets clear</c> — drop every extra caret</item>
    ///   <item><c>:carets above</c> / <c>below</c> — one new caret on the
    ///         line above / below the primary</item>
    ///   <item><c>:carets line</c> — one caret per line in the selection</item>
    ///   <item><c>:carets sel &lt;pat&gt;</c> — seed at every match inside
    ///         the current selection</item>
    ///   <item><c>:carets &lt;pat&gt;</c> — seed at every match in the buffer</item>
    /// </list>
    /// Patterns are plain regex (.NET syntax). Use <c>:carets sel ^</c> as a
    /// shorthand for "one caret per line" — both forms work.
    /// </summary>
    private void CaretsCommand(string arg)
    {
        arg = (arg ?? string.Empty).Trim();
        if (arg.Length == 0 || arg.Equals("count", StringComparison.Ordinal))
        {
            _message = $"{_buffer.ExtraCaretCount + 1} caret(s) active";
            return;
        }

        switch (arg)
        {
            case "clear":
                if (_buffer.ExtraCaretCount == 0) { _message = "no extra carets"; return; }
                var n = _buffer.ExtraCaretCount;
                _buffer.ClearExtraCarets();
                _message = $"cleared {n} extra caret(s)";
                return;
            case "above":
                AddCaretAbove();
                return;
            case "below":
                AddCaretBelow();
                return;
            case "line":
                SeedCaretsOnSelectedLines();
                return;
        }

        if (arg.StartsWith("sel ", StringComparison.Ordinal))
        {
            SeedCaretsByPattern(arg[4..].Trim(), inSelectionOnly: true);
            return;
        }

        // Anything else is treated as a buffer-wide pattern.
        SeedCaretsByPattern(arg, inSelectionOnly: false);
    }

    /// <summary>One caret at column 0 of every line covered by the current selection.</summary>
    private void SeedCaretsOnSelectedLines()
    {
        var sel = _buffer.Selection;
        if (sel is null) { _message = "no selection — select multiple lines first"; return; }
        var (start, end) = sel.Value;

        _buffer.ClearExtraCarets();
        _buffer.ClearSelection();
        _buffer.MoveCursor(new TextLocation(start.Line, 0));
        var added = 0;
        for (var line = start.Line + 1; line <= end.Line; line++)
        {
            _buffer.AddCaret(new TextLocation(line, 0));
            added++;
        }
        _message = added > 0 ? $"{added + 1} carets" : "only one line in selection";
    }

    /// <summary>Seed an extra caret at every regex match in the buffer (or selection).</summary>
    private void SeedCaretsByPattern(string pattern, bool inSelectionOnly)
    {
        if (string.IsNullOrEmpty(pattern)) { _message = "carets: pattern required"; return; }

        Regex rx;
        try { rx = new Regex(pattern, RegexOptions.Compiled); }
        catch (ArgumentException ex) { _message = $"carets: bad pattern: {ex.Message}"; return; }

        int firstLine, lastLine;
        if (inSelectionOnly)
        {
            var sel = _buffer.Selection;
            if (sel is null) { _message = "carets sel: no active selection"; return; }
            firstLine = sel.Value.Start.Line;
            lastLine = sel.Value.End.Line;
        }
        else
        {
            firstLine = 0;
            lastLine = _buffer.LineCount - 1;
        }

        _buffer.ClearExtraCarets();
        _buffer.ClearSelection();
        var hits = new List<TextLocation>();
        for (var li = firstLine; li <= lastLine; li++)
        {
            var line = _buffer.GetLine(li);
            foreach (Match m in rx.Matches(line))
            {
                if (m.Length == 0) continue; // skip zero-width to avoid infinite-seed
                hits.Add(new TextLocation(li, m.Index));
            }
        }

        if (hits.Count == 0) { _message = $"carets: no matches for /{pattern}/"; return; }

        _buffer.MoveCursor(hits[0]);
        for (var i = 1; i < hits.Count; i++)
            _buffer.AddCaret(hits[i]);
        _message = $"{hits.Count} caret(s) from /{pattern}/";
    }
}
