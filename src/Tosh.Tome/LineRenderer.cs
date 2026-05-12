using System.Text;
using Tosh.Tui.Editing;

namespace Tosh.Tome;

/// <summary>
/// Per-line render decorations: bracket-match highlighting, current-line
/// background, trailing-whitespace dim. Walks the characters in a viewport
/// slice and emits ANSI runs with style state that composes correctly with
/// the colorizer's foreground spans, selection reverse-video, and the
/// caller-supplied background. Each ANSI reset re-applies the background
/// when the line is the current cursor line, so the highlight is preserved
/// underneath syntax colors.
/// </summary>
internal static class LineRenderer
{
    private const string Reset = "\u001b[0m";
    private const string SelOpen = "\u001b[7m";
    private const string SelClose = "\u001b[27m";
    private const string MatchOpen = "\u001b[1;4m"; // bold + underline — bracket pair
    private const string MatchClose = "\u001b[22;24m";
    private const string DimOpen = "\u001b[2m";
    private const string DimClose = "\u001b[22m";
    private const string CurrentLineBg = "\u001b[48;5;236m"; // subtle dark grey
    private const string DefaultBgReset = "\u001b[49m";

    public sealed record LineDecorations(
        int SelStart,
        int SelEnd,
        int BracketMatchCol,
        int TrailingWsStart,
        bool IsCurrentLine,
        bool NewlineSelected);

    public static void RenderLine(
        StringBuilder sb,
        string line,
        int lineIndex,
        int scrollColumn,
        int viewportWidth,
        IReadOnlyList<StyledSpan> spans,
        LineDecorations deco)
    {
        // Each row begins with a CSI 2K clear in the caller; emit the
        // line-BG marker first so the cleared cells inherit the highlight,
        // then re-emit it after the line content to extend it past the
        // text to the right edge.
        if (deco.IsCurrentLine)
            sb.Append(CurrentLineBg);

        var visibleStart = scrollColumn;
        var visibleEnd = Math.Min(line.Length, scrollColumn + viewportWidth);

        if (line.Length == 0 || scrollColumn >= line.Length)
        {
            if (deco.NewlineSelected && viewportWidth > 0)
                sb.Append(SelOpen).Append(' ').Append(SelClose);
            if (deco.IsCurrentLine)
                sb.Append("\u001b[K").Append(DefaultBgReset);
            return;
        }

        var cursor = visibleStart;
        foreach (var span in spans)
        {
            if (span.End <= visibleStart) continue;
            if (span.Start >= visibleEnd) break;

            if (span.Start > cursor)
            {
                var gapEnd = Math.Min(span.Start, visibleEnd);
                EmitRange(sb, line, cursor, gapEnd, null, deco);
                cursor = gapEnd;
                if (cursor >= visibleEnd) goto AfterSpans;
            }
            var runStart = Math.Max(span.Start, cursor);
            var runEnd = Math.Min(span.End, visibleEnd);
            if (runEnd > runStart)
            {
                EmitRange(sb, line, runStart, runEnd, span.AnsiOpen, deco);
                cursor = runEnd;
            }
        }
        if (cursor < visibleEnd)
            EmitRange(sb, line, cursor, visibleEnd, null, deco);

    AfterSpans:
        if (deco.NewlineSelected && visibleEnd >= line.Length && (visibleEnd - visibleStart) < viewportWidth)
            sb.Append(SelOpen).Append(' ').Append(SelClose);

        // Extend the current-line BG to the right edge of the viewport.
        if (deco.IsCurrentLine)
            sb.Append("\u001b[K").Append(DefaultBgReset);
    }

    private static void EmitRange(
        StringBuilder sb,
        string line,
        int start,
        int end,
        string? colorAnsi,
        LineDecorations deco)
    {
        // Character-by-character so we can override per-position decorations
        // (trailing-WS dot, bracket-match underline) while honoring the
        // colorizer span and selection state. Runs are tiny (<= viewport
        // width) so per-char ANSI overhead is fine.
        for (var i = start; i < end; i++)
        {
            var ch = line[i];
            var inSel = deco.SelStart >= 0 && i >= deco.SelStart && i < deco.SelEnd;
            var isMatch = i == deco.BracketMatchCol;
            var isTrailingWs = i >= deco.TrailingWsStart && (ch == ' ' || ch == '\t');

            if (inSel) sb.Append(SelOpen);
            if (isMatch) sb.Append(MatchOpen);

            if (isTrailingWs)
            {
                // Render trailing whitespace as a visible glyph in dim style so the
                // user can see it without changing the underlying buffer. Tab is
                // rendered as a single ‹»› marker; spaces become ‹·›.
                sb.Append(DimOpen);
                sb.Append(ch == '\t' ? '»' : '·');
                sb.Append(DimClose);
            }
            else
            {
                if (colorAnsi is not null) sb.Append(colorAnsi);
                sb.Append(ch);
                if (colorAnsi is not null) sb.Append(Reset);
                // Re-apply the line BG if a reset just cleared it.
                if (colorAnsi is not null && deco.IsCurrentLine) sb.Append(CurrentLineBg);
            }

            if (isMatch) sb.Append(MatchClose);
            if (inSel) sb.Append(SelClose);
        }
    }
}
