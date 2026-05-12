using System.Text;
using Tosh.Tui.Editing;

namespace Tosh.Tome;

/// <summary>
/// Renders a left gutter for every buffer line, modeled on the REPL's multi-line
/// continuation gutter. Each row gets a right-padded line number, depth bars
/// (one '│' per level of brace nesting at the start of the line), an optional
/// transition glyph for lines that open or close a block, and a final '│'
/// separator dividing the gutter from the text area.
///
/// State is rebuilt per frame from a snapshot of the buffer — cheap because a
/// single pass over all lines suffices and frames only happen on keystrokes.
/// </summary>
internal sealed class GutterRenderer
{
    private const string DimOpen = "\u001b[2m";          // faint
    private const string CurrentLineOpen = "\u001b[38;5;215m"; // orange — current line number
    private const string DiagErrorOpen = "\u001b[38;5;203m\u001b[1m"; // bold red — error line number
    private const string DiagWarnOpen = "\u001b[38;5;221m\u001b[1m";  // bold yellow — warning line number
    private const string DiagInfoOpen = "\u001b[38;5;110m";          // soft blue — info line number
    private const string Reset = "\u001b[0m";

    private static readonly GutterGlyphs Glyphs = new(
        Vertical: '│',
        Open: '╮',
        Close: '╯',
        Join: '├',
        Transition: '┤',
        Dot: '·');

    private readonly GutterMarker[] _markers;
    private readonly int[] _depths;
    private readonly int _maxDepth;
    private readonly int _lineNumberWidth;
    private readonly IReadOnlyDictionary<int, int>? _severityByLine;

    public GutterRenderer(TextBuffer buffer, IReadOnlyDictionary<int, int>? severityByLine = null)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        _severityByLine = severityByLine;

        var lineCount = buffer.LineCount;
        _markers = new GutterMarker[lineCount];
        _depths = new int[lineCount];

        // Per-line gutter classification mirrors the REPL's continuation gutter:
        // walk lines tracking brace depth, mark openers/closers/transitions.
        var depth = 0;
        var maxDepth = 0;

        for (var i = 0; i < lineCount; i++)
        {
            var line = buffer.GetLine(i);
            var previousLine = i > 0 ? buffer.GetLine(i - 1) : string.Empty;

            var startsWithCloser = StartsWithCloserToken(line);
            var endsWithOpener = EndsWithOpenerToken(line);
            var previousEndsWithOpener = EndsWithOpenerToken(previousLine);

            var effectiveDepth = Math.Max(0, depth - (startsWithCloser ? 1 : 0));
            if (previousEndsWithOpener && !startsWithCloser)
                effectiveDepth = Math.Max(1, effectiveDepth);

            _depths[i] = effectiveDepth;
            _markers[i] = SelectGutterMarker(startsWithCloser, endsWithOpener, effectiveDepth);

            if (effectiveDepth > maxDepth) maxDepth = effectiveDepth;

            depth = Math.Max(0, depth + ComputeBraceDelta(line));
        }

        _maxDepth = maxDepth;
        _lineNumberWidth = Math.Max(2, lineCount.ToString().Length);
    }

    /// <summary>
    /// Total gutter width in cells. Layout: [line number][space][depth bars + marker][│][space].
    /// At minimum: 2-digit line number + space + 1 marker col + │ + space = 6.
    /// </summary>
    public int Width => _lineNumberWidth + 1 + Math.Max(1, _maxDepth + 1) + 1 + 1;

    public string Render(int lineIndex, bool isCurrentLine)
    {
        if (lineIndex < 0 || lineIndex >= _markers.Length)
            return RenderEmpty();

        var sb = new StringBuilder(Width + 16);

        // Line number, right-aligned in _lineNumberWidth, then a space. The
        // colour escalates with diagnostic severity so an at-a-glance scan of
        // the gutter shows where problems live.
        var lineNumber = (lineIndex + 1).ToString();
        var pad = _lineNumberWidth - lineNumber.Length;
        var sev = _severityByLine is not null && _severityByLine.TryGetValue(lineIndex, out var s) ? s : 0;
        var numberOpen = sev switch
        {
            1 => DiagErrorOpen,
            2 => DiagWarnOpen,
            3 or 4 => DiagInfoOpen,
            _ => isCurrentLine ? CurrentLineOpen : DimOpen,
        };
        sb.Append(numberOpen);
        for (var i = 0; i < pad; i++) sb.Append(' ');
        sb.Append(lineNumber);
        sb.Append(Reset);
        sb.Append(' ');

        // Depth bars + marker, dim.
        sb.Append(DimOpen);
        sb.Append(BuildDepthCells(_markers[lineIndex], _depths[lineIndex]));
        sb.Append(Reset);

        // Right border separator and trailing space.
        sb.Append(DimOpen).Append(Glyphs.Vertical).Append(Reset);
        sb.Append(' ');

        return sb.ToString();
    }

    private string RenderEmpty()
    {
        // For rows past end-of-buffer: blank gutter cells then dim '│' separator.
        var sb = new StringBuilder(Width);
        sb.Append(new string(' ', Width - 2));
        sb.Append(DimOpen).Append(Glyphs.Vertical).Append(Reset);
        sb.Append(' ');
        return sb.ToString();
    }

    private string BuildDepthCells(GutterMarker marker, int depth)
    {
        // Width = max(1, _maxDepth + 1). Bars fill positions [0..bars-1], marker at markerColumn.
        var cells = Math.Max(1, _maxDepth + 1);
        var chars = new char[cells];
        for (var i = 0; i < cells; i++) chars[i] = ' ';

        var bars = marker == GutterMarker.Vertical
            ? Math.Clamp(depth - 1, 0, cells - 1)
            : Math.Clamp(depth, 0, cells - 1);

        for (var i = 0; i < bars; i++) chars[i] = Glyphs.Vertical;

        var markerColumn = marker == GutterMarker.Vertical
            ? Math.Clamp(depth - 1, 0, cells - 1)
            : Math.Min(bars, cells - 1);

        if (marker == GutterMarker.Dot)
            markerColumn = 0;

        chars[markerColumn] = marker switch
        {
            GutterMarker.Open => Glyphs.Open,
            GutterMarker.Close => Glyphs.Close,
            GutterMarker.Transition => Glyphs.Transition,
            GutterMarker.Dot => Glyphs.Dot,
            _ => Glyphs.Vertical,
        };

        if (marker is GutterMarker.Open or GutterMarker.Close or GutterMarker.Transition && depth > 0)
        {
            var joinColumn = Math.Clamp(depth - 1, 0, cells - 1);
            if (joinColumn != markerColumn)
                chars[joinColumn] = Glyphs.Join;
        }

        return new string(chars);
    }

    private static bool StartsWithCloserToken(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.Length > 0 && trimmed[0] == '}';
    }

    private static bool EndsWithOpenerToken(string line)
    {
        var trimmed = line.TrimEnd();
        return trimmed.EndsWith('{');
    }

    private static GutterMarker SelectGutterMarker(bool startsWithCloser, bool endsWithOpener, int depth)
    {
        if (startsWithCloser && endsWithOpener) return GutterMarker.Transition;
        if (startsWithCloser) return GutterMarker.Close;
        if (endsWithOpener) return GutterMarker.Open;
        if (depth > 0) return GutterMarker.Vertical;
        return GutterMarker.Dot;
    }

    private static int ComputeBraceDelta(string line)
    {
        var delta = 0;
        var inSingle = false;
        var inDouble = false;
        var escaping = false;
        var inComment = false;

        foreach (var ch in line)
        {
            if (inComment) continue;

            if (inSingle)
            {
                if (escaping) { escaping = false; continue; }
                if (ch == '\\') { escaping = true; continue; }
                if (ch == '\'') inSingle = false;
                continue;
            }

            if (inDouble)
            {
                if (escaping) { escaping = false; continue; }
                if (ch == '\\') { escaping = true; continue; }
                if (ch == '"') inDouble = false;
                continue;
            }

            switch (ch)
            {
                case '#': inComment = true; break;
                case '\'': inSingle = true; break;
                case '"': inDouble = true; break;
                case '{': delta++; break;
                case '}': delta--; break;
            }
        }

        return delta;
    }

    private readonly record struct GutterGlyphs(char Vertical, char Open, char Close, char Join, char Transition, char Dot);

    private enum GutterMarker { Dot, Vertical, Open, Close, Transition }
}
