using System.Text;
using Tosh.Tome.Theme;
using Tosh.Tui.Editing;

namespace Tosh.Tome;

/// <summary>
/// Per-frame state consumed by <see cref="GutterRenderer"/>. Pass-through
/// dictionaries / sets are read-only; callers retain ownership. All
/// fields are optional — the renderer treats a missing collection as
/// "no annotations of that kind on any line".
/// </summary>
internal sealed class GutterContext
{
    public IReadOnlyDictionary<int, int>? SeverityByLine { get; init; }
    public IReadOnlySet<int>? Breakpoints { get; init; }
    public IReadOnlySet<int>? ExtraCaretLines { get; init; }
    public (int Start, int End)? SelectionLineRange { get; init; }
    public IReadOnlyDictionary<int, DiffKind>? DiffLines { get; init; }
    public IReadOnlySet<int>? SearchHitLines { get; init; }
}

/// <summary>
/// Renders the left gutter for every buffer line. Layout, left to right:
/// <list type="bullet">
///   <item>1 cell — breakpoint <c>●</c> or extra-caret <c>+</c> indicator</item>
///   <item>line number, right-padded, severity-coloured</item>
///   <item>1 space</item>
///   <item>depth bars + per-line marker (block opener/closer/transition, or
///     a diagnostic glyph that overrides the dot when the line has issues)</item>
///   <item>1 cell — selection / diff / search-hit annotation</item>
///   <item><c>│</c> separator + trailing space</item>
/// </list>
/// Brace depths and opener/closer flags are pulled from
/// <see cref="TextBuffer.GetBraceLineInfo"/> (cached on the buffer by
/// <see cref="TextBuffer.Revision"/>) so the renderer no longer rescans
/// every line for braces/strings/comments on every frame.
/// </summary>
internal sealed class GutterRenderer
{
    private const string DimOpen = "\u001b[2m";
    private const string Reset = "\u001b[0m";
    private static readonly Role[] DepthRoles =
    {
        Role.GutterDepth1, Role.GutterDepth2, Role.GutterDepth3,
        Role.GutterDepth4, Role.GutterDepth5,
    };
    private static string DepthOpen(int col) => TomeTheme.Active.Open(DepthRoles[col % DepthRoles.Length]);

    private static string CurrentLineOpen => TomeTheme.Active.Open(Role.GutterCurrentLine);
    private static string DiagErrorOpen => TomeTheme.Active.Open(Role.GutterDiagError);
    private static string DiagWarnOpen => TomeTheme.Active.Open(Role.GutterDiagWarn);
    private static string DiagInfoOpen => TomeTheme.Active.Open(Role.GutterDiagInfo);
    private static string DiffAddedOpen => TomeTheme.Active.Open(Role.GutterDiffAdded);
    private static string DiffModifiedOpen => TomeTheme.Active.Open(Role.GutterDiffModified);
    private static string DiffDeletedOpen => TomeTheme.Active.Open(Role.GutterDiffDeleted);
    private static string SelectionOpen => TomeTheme.Active.Open(Role.GutterSelection);
    private static string SearchHitOpen => TomeTheme.Active.Open(Role.GutterSearchHit);
    private static string BreakpointOpen => TomeTheme.Active.Open(Role.GutterBreakpoint);
    private static string MultiCaretOpen => TomeTheme.Active.Open(Role.GutterMultiCaret);

    private static readonly GutterGlyphs Glyphs = new(
        Vertical: '│',
        Open: '╮',
        Close: '╯',
        Join: '├',
        Transition: '┤',
        Dot: '·');

    private const char BreakpointGlyph = '●';
    private const char MultiCaretGlyph = '+';
    private const char SelectionBarGlyph = '▌';
    private const char DiffBarGlyph = '▍';
    private const char SearchHitGlyph = '◆';
    private const char DiagErrorGlyph = '⚠';
    private const char DiagWarnGlyph = '!';
    private const char DiagInfoGlyph = 'i';

    private readonly GutterMarker[] _markers;
    private readonly int[] _depths;
    private readonly int _maxDepth;
    private readonly int _lineNumberWidth;
    private readonly GutterContext _ctx;

    public GutterRenderer(TextBuffer buffer, GutterContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        _ctx = context ?? new GutterContext();

        var lineCount = buffer.LineCount;
        _markers = new GutterMarker[lineCount];
        _depths = new int[lineCount];

        var braceInfo = buffer.GetBraceLineInfo();
        var maxDepth = 0;

        for (var i = 0; i < lineCount; i++)
        {
            var info = braceInfo[i];
            _depths[i] = info.Depth;
            _markers[i] = SelectGutterMarker(info.StartsWithCloser, info.EndsWithOpener, info.Depth);

            // Blank lines suppress the dot — keeps the gutter visually quiet
            // in the long whitespace runs that pad most source files.
            if (_markers[i] == GutterMarker.Dot && string.IsNullOrWhiteSpace(buffer.GetLine(i)))
                _markers[i] = GutterMarker.Blank;

            if (info.Depth > maxDepth) maxDepth = info.Depth;
        }

        _maxDepth = maxDepth;
        _lineNumberWidth = Math.Max(2, lineCount.ToString().Length);
    }

    /// <summary>
    /// Total gutter width in cells. Layout: [left marker][line number]
    /// [space][depth bars + marker][right bar][│][space].
    /// </summary>
    public int Width => 1 + _lineNumberWidth + 1 + Math.Max(1, _maxDepth + 1) + 1 + 1 + 1;

    public string Render(int lineIndex, bool isCurrentLine)
    {
        if (lineIndex < 0 || lineIndex >= _markers.Length)
            return RenderEmpty();

        var sb = new StringBuilder(Width + 32);

        // ── Left marker cell: breakpoint > extra-caret > blank.
        AppendLeftMarker(sb, lineIndex);

        // ── Line number, severity-coloured (or dim/current-line on clean rows).
        AppendLineNumber(sb, lineIndex, isCurrentLine);
        sb.Append(' ');

        // ── Depth bars + per-line marker. The dot is replaced by a
        // diagnostic glyph when the line carries an issue.
        AppendDepthCells(sb, lineIndex);

        // ── Right marker cell: selection > diff > search hit > blank.
        AppendRightMarker(sb, lineIndex);

        // ── Separator + trailing space.
        sb.Append(DimOpen).Append(Glyphs.Vertical).Append(Reset);
        sb.Append(' ');

        return sb.ToString();
    }

    private string RenderEmpty()
    {
        var sb = new StringBuilder(Width);
        sb.Append(new string(' ', Width - 2));
        sb.Append(DimOpen).Append(Glyphs.Vertical).Append(Reset);
        sb.Append(' ');
        return sb.ToString();
    }

    private void AppendLeftMarker(StringBuilder sb, int lineIndex)
    {
        if (_ctx.Breakpoints is not null && _ctx.Breakpoints.Contains(lineIndex))
        {
            sb.Append(BreakpointOpen).Append(BreakpointGlyph).Append(Reset);
            return;
        }
        if (_ctx.ExtraCaretLines is not null && _ctx.ExtraCaretLines.Contains(lineIndex))
        {
            sb.Append(MultiCaretOpen).Append(MultiCaretGlyph).Append(Reset);
            return;
        }
        sb.Append(' ');
    }

    private void AppendLineNumber(StringBuilder sb, int lineIndex, bool isCurrentLine)
    {
        var lineNumber = (lineIndex + 1).ToString();
        var pad = _lineNumberWidth - lineNumber.Length;
        var sev = _ctx.SeverityByLine is not null && _ctx.SeverityByLine.TryGetValue(lineIndex, out var s) ? s : 0;
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
    }

    private void AppendDepthCells(StringBuilder sb, int lineIndex)
    {
        var marker = _markers[lineIndex];
        var depth = _depths[lineIndex];
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
        if (marker == GutterMarker.Dot || marker == GutterMarker.Blank)
            markerColumn = 0;

        // Diagnostic glyph replaces the dot/blank when severity is set,
        // so the marker column doubles as the "this line has an issue"
        // indicator without consuming an extra slot.
        var sev = _ctx.SeverityByLine is not null && _ctx.SeverityByLine.TryGetValue(lineIndex, out var s) ? s : 0;
        var diagGlyph = sev switch
        {
            1 => DiagErrorGlyph,
            2 => DiagWarnGlyph,
            3 or 4 => DiagInfoGlyph,
            _ => '\0',
        };

        var markerGlyph = marker switch
        {
            GutterMarker.Open => Glyphs.Open,
            GutterMarker.Close => Glyphs.Close,
            GutterMarker.Transition => Glyphs.Transition,
            GutterMarker.Dot => Glyphs.Dot,
            GutterMarker.Blank => ' ',
            _ => Glyphs.Vertical,
        };
        if (diagGlyph != '\0' && marker is GutterMarker.Dot or GutterMarker.Blank or GutterMarker.Vertical)
            markerGlyph = diagGlyph;

        chars[markerColumn] = markerGlyph;
        if (marker is GutterMarker.Open or GutterMarker.Close or GutterMarker.Transition && depth > 0)
        {
            var joinColumn = Math.Clamp(depth - 1, 0, cells - 1);
            if (joinColumn != markerColumn)
                chars[joinColumn] = Glyphs.Join;
        }

        // Emit each cell with its depth-level colour. Diagnostic glyphs
        // override their column's colour with the severity colour instead.
        for (var col = 0; col < cells; col++)
        {
            var c = chars[col];
            if (c == ' ') { sb.Append(' '); continue; }
            if (col == markerColumn && diagGlyph != '\0' && c == diagGlyph)
            {
                var diagOpen = sev switch { 1 => DiagErrorOpen, 2 => DiagWarnOpen, _ => DiagInfoOpen };
                sb.Append(diagOpen).Append(c).Append(Reset);
            }
            else
            {
                sb.Append(DepthOpen(col)).Append(c).Append(Reset);
            }
        }
    }

    private void AppendRightMarker(StringBuilder sb, int lineIndex)
    {
        // Priority: selection > diff > search-hit.
        if (_ctx.SelectionLineRange is { } range
            && lineIndex >= range.Start && lineIndex <= range.End)
        {
            sb.Append(SelectionOpen).Append(SelectionBarGlyph).Append(Reset);
            return;
        }
        if (_ctx.DiffLines is not null && _ctx.DiffLines.TryGetValue(lineIndex, out var kind))
        {
            var open = kind switch
            {
                DiffKind.Added => DiffAddedOpen,
                DiffKind.Modified => DiffModifiedOpen,
                DiffKind.Deleted => DiffDeletedOpen,
                _ => DimOpen,
            };
            sb.Append(open).Append(DiffBarGlyph).Append(Reset);
            return;
        }
        if (_ctx.SearchHitLines is not null && _ctx.SearchHitLines.Contains(lineIndex))
        {
            sb.Append(SearchHitOpen).Append(SearchHitGlyph).Append(Reset);
            return;
        }
        sb.Append(' ');
    }

    private static GutterMarker SelectGutterMarker(bool startsWithCloser, bool endsWithOpener, int depth)
    {
        if (startsWithCloser && endsWithOpener) return GutterMarker.Transition;
        if (startsWithCloser) return GutterMarker.Close;
        if (endsWithOpener) return GutterMarker.Open;
        if (depth > 0) return GutterMarker.Vertical;
        return GutterMarker.Dot;
    }

    private readonly record struct GutterGlyphs(char Vertical, char Open, char Close, char Join, char Transition, char Dot);

    private enum GutterMarker { Dot, Vertical, Open, Close, Transition, Blank }
}
