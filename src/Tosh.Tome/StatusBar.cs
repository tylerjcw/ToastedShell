using System.Text;
using Tosh.LanguageServices;
using Tosh.Tome.Settings;
using Tosh.Tui.Editing;

namespace Tosh.Tome;

/// <summary>
/// Builds the editor status bar from current document state and the
/// user's <see cref="TomeSettings"/>. Output is a single fully-styled
/// string of exactly <c>width</c> visible columns; the caller writes
/// it directly (no extra wrapping in reverse video — every segment
/// owns its own foreground/background).
/// </summary>
internal static class StatusBar
{
    public readonly record struct Inputs(
        TomeApp.EditorMode Mode,
        string FilePath,
        string DisplayName,
        bool IsModified,
        bool FocusExplorer,
        string? WorkspaceName,
        TextLocation Cursor,
        int LineCount,
        int? SelectionLength,
        IReadOnlyList<LspDiagnostic> Diagnostics,
        int ActiveTab,
        int TabCount);

    public static string Render(Inputs i, TomeSettings settings, int width)
    {
        var s = settings.StatusBar;
        var bodyBg = $"\u001b[48;5;{s.BackgroundColor}m";
        var bodyFg = $"\u001b[38;5;{s.ForegroundColor}m";
        var reset = "\u001b[0m";
        var sepStr = $"{bodyBg}\u001b[38;5;{s.SeparatorColor}m {s.Separator} {bodyFg}";
        var sepVis = 3; // " │ "

        var left = new List<Segment>();
        var right = new List<Segment>();

        // ─── Mode block (or EXPLORER when the explorer has focus) ─────
        if (s.ShowMode)
        {
            if (i.FocusExplorer)
            {
                left.Add(new Segment($"\u001b[1m\u001b[48;5;{s.CommandModeBg}m\u001b[38;5;{s.CommandModeFg}m EXPLORE \u001b[22m", " EXPLORE ".Length));
            }
            else
            {
                var bg = i.Mode == TomeApp.EditorMode.Edit ? s.EditModeBg : s.CommandModeBg;
                var fg = i.Mode == TomeApp.EditorMode.Edit ? s.EditModeFg : s.CommandModeFg;
                var label = i.Mode == TomeApp.EditorMode.Edit ? " EDIT " : " CMD  ";
                left.Add(new Segment($"\u001b[1m\u001b[48;5;{bg}m\u001b[38;5;{fg}m{label}\u001b[22m", label.Length));
            }
        }

        // ─── Workspace name banner (only when one is loaded) ──────────
        if (!string.IsNullOrEmpty(i.WorkspaceName))
        {
            AddSep(left, sepStr, sepVis);
            var text = $" {i.WorkspaceName} ";
            left.Add(new Segment($"{bodyBg}\u001b[38;5;{s.GitColor}m{text}", text.Length));
        }

        // ─── Git branch ──────────────────────────────────────────────
        if (s.ShowGit)
        {
            var branch = GitInfo.GetBranch(string.IsNullOrEmpty(i.FilePath) ? null : i.FilePath);
            if (!string.IsNullOrEmpty(branch))
            {
                AddSep(left, sepStr, sepVis);
                var text = $"{s.GitGlyph} {branch}";
                left.Add(new Segment($"{bodyBg}\u001b[38;5;{s.GitColor}m{text}", text.Length));
            }
        }

        // ─── File name + modified marker ──────────────────────────────
        if (s.ShowFile)
        {
            AddSep(left, sepStr, sepVis);
            var name = i.DisplayName;
            left.Add(new Segment($"{bodyBg}{bodyFg}\u001b[1m{name}\u001b[22m", name.Length));
            if (s.ShowModified && i.IsModified)
            {
                var glyph = $" \u001b[38;5;{s.ModifiedColor}m{s.ModifiedGlyph}{bodyFg}";
                left.Add(new Segment(glyph, 1 + s.ModifiedGlyph.Length));
            }
        }

        // ─── Diagnostic counts (left side, near filename) ─────────────
        if (s.ShowDiagnostics && i.Diagnostics.Count > 0)
        {
            int errors = 0, warnings = 0;
            foreach (var d in i.Diagnostics)
            {
                if (d.Severity == 1) errors++;
                else if (d.Severity == 2) warnings++;
            }
            if (errors > 0 || warnings > 0)
            {
                var sb = new StringBuilder();
                sb.Append(bodyBg).Append("  ");
                var visLen = 2;
                if (errors > 0)
                {
                    var text = $"{s.ErrorGlyph} {errors}";
                    sb.Append($"\u001b[38;5;{s.ErrorColor}m").Append(text);
                    visLen += text.Length;
                }
                if (warnings > 0)
                {
                    if (errors > 0) { sb.Append(' '); visLen++; }
                    var text = $"{s.WarningGlyph} {warnings}";
                    sb.Append($"\u001b[38;5;{s.WarningColor}m").Append(text);
                    visLen += text.Length;
                }
                sb.Append(bodyFg);
                left.Add(new Segment(sb.ToString(), visLen));
            }
        }

        // ─── Right side ───────────────────────────────────────────────
        // Selection length
        if (s.ShowSelection && i.SelectionLength is int len && len > 0)
        {
            var text = $"sel:{len}";
            right.Add(new Segment($"{bodyBg}{bodyFg}{text}", text.Length));
            AddSep(right, sepStr, sepVis);
        }

        // Language
        if (s.ShowLanguage)
        {
            var lang = LanguageInfo.Resolve(i.FilePath);
            var text = lang;
            right.Add(new Segment($"{bodyBg}\u001b[38;5;{s.LanguageColor}m{text}{bodyFg}", text.Length));
            AddSep(right, sepStr, sepVis);
        }

        // Tabs counter
        if (s.ShowTabs && i.TabCount > 1)
        {
            var text = $"tab {i.ActiveTab + 1}/{i.TabCount}";
            right.Add(new Segment($"{bodyBg}{bodyFg}{text}", text.Length));
            AddSep(right, sepStr, sepVis);
        }

        // Position / percent / line-count
        if (s.ShowPosition)
        {
            var text = i.Cursor.ToString();
            right.Add(new Segment($"{bodyBg}{bodyFg}{text}", text.Length));
        }
        if (s.ShowLineCount)
        {
            if (s.ShowPosition) { right.Add(new Segment($"{bodyBg}{bodyFg} ", 1)); }
            var text = $"of {i.LineCount}";
            right.Add(new Segment($"{bodyBg}\u001b[2m{text}\u001b[22m{bodyFg}", text.Length));
        }
        if (s.ShowPercent)
        {
            var pct = i.LineCount <= 1
                ? 100
                : (int)Math.Round(100.0 * (i.Cursor.Line) / Math.Max(1, i.LineCount - 1));
            pct = Math.Clamp(pct, 0, 100);
            var text = $" {pct,3}%";
            right.Add(new Segment($"{bodyBg}{bodyFg}{text}", text.Length));
        }

        // ─── Assemble ────────────────────────────────────────────────
        var leftWidth = 0;
        foreach (var seg in left) leftWidth += seg.VisibleLength;
        var rightWidth = 0;
        foreach (var seg in right) rightWidth += seg.VisibleLength;
        // Pad both sides with one space of body bg so segments don't kiss the edge.
        const int edgePad = 1;
        var total = leftWidth + rightWidth + edgePad * 2;
        var padding = Math.Max(1, width - total);

        var outSb = new StringBuilder(width + 64);
        outSb.Append(bodyBg).Append(bodyFg);
        outSb.Append(' '); // edge pad
        foreach (var seg in left) outSb.Append(seg.Text);
        outSb.Append(bodyBg).Append(bodyFg).Append(new string(' ', padding));
        foreach (var seg in right) outSb.Append(seg.Text);
        outSb.Append(bodyBg).Append(bodyFg).Append(' ');
        outSb.Append(reset);
        return outSb.ToString();
    }

    private static void AddSep(List<Segment> list, string sepStr, int sepVis)
    {
        if (list.Count == 0) return;
        list.Add(new Segment(sepStr, sepVis));
    }

    private readonly record struct Segment(string Text, int VisibleLength);
}
