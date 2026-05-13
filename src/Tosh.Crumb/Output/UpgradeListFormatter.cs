using System.Text;

namespace Tosh.Crumb.Output;

/// <summary>
/// Renders pending-upgrade lists (repo + AUR) as boxed, colorized
/// tables matching the TōSh built-in table renderer (rounded corners,
/// header row, alternating column tints).
/// </summary>
internal static class UpgradeListFormatter
{
    private const string Reset = "\u001b[0m";

    private static readonly (byte r, byte g, byte b, byte idx) BorderColor = (0x5F, 0x5F, 0x5F, 59);
    private static readonly (byte r, byte g, byte b, byte idx) HeaderColor = (0xFF, 0xD7, 0x5F, 221);
    private static readonly (byte r, byte g, byte b, byte idx) RepoTint = (0x87, 0xAF, 0xFF, 111);
    private static readonly (byte r, byte g, byte b, byte idx) AurTint = (0x5F, 0xD7, 0xFF, 81);
    private static readonly (byte r, byte g, byte b, byte idx) NameColor = (0x87, 0xD7, 0xFF, 117);
    private static readonly (byte r, byte g, byte b, byte idx) OldColor = (0xAF, 0xAF, 0xAF, 145);
    private static readonly (byte r, byte g, byte b, byte idx) NewColor = (0x87, 0xFF, 0x87, 120);

    public static void RenderRepoUpgrades(
        IReadOnlyList<string> rawLines,
        IReadOnlyDictionary<string, string>? repoByName = null)
    {
        if (rawLines.Count == 0) return;
        var rows = new List<(string Repo, string Name, string From, string To)>(rawLines.Count);
        foreach (var line in rawLines)
        {
            if (TryParse(line, out var name, out var from, out var to))
            {
                var repo = repoByName is not null && repoByName.TryGetValue(name, out var r) ? r : "sync";
                rows.Add((repo, name, from, to));
            }
            else
            {
                rows.Add((string.Empty, line, string.Empty, string.Empty));
            }
        }
        Confirm.Status(string.Empty);
        Confirm.Status($"Packages to upgrade ({rows.Count})");
        RenderTable(rows, RepoTint);
    }

    public static void RenderAurUpgrades(IReadOnlyList<(string Name, string From, string To)> upgrades)
    {
        if (upgrades.Count == 0) return;
        var rows = upgrades.Select(u => ("aur", u.Name, u.From, u.To)).ToList();
        Confirm.Status(string.Empty);
        Confirm.Status($"AUR packages to upgrade ({rows.Count})");
        RenderTable(rows, AurTint);
    }

    /// <summary>
    /// Render a 3-column "Source | Name | Version" boxed table. Used for
    /// install and removal confirmation lists. Source column is tinted
    /// per row: "aur" gets the AUR tint, anything else gets the repo tint.
    /// </summary>
    public static void RenderPlan(
        IReadOnlyList<(string Source, string Name, string Version)> rows,
        string title)
    {
        if (rows.Count == 0) return;
        var truecolor = SupportsTrueColor();
        var color = ColorEnabled();

        string[] headers = { "Source", "Name", "Version" };
        var widths = new[]
        {
            Math.Max(headers[0].Length, rows.Max(r => r.Source.Length)),
            Math.Max(headers[1].Length, rows.Max(r => r.Name.Length)),
            Math.Max(headers[2].Length, rows.Max(r => r.Version.Length)),
        };

        Confirm.Status(string.Empty);
        Confirm.Status($"{title} ({rows.Count})");
        Confirm.Status(BuildBorder(widths, "╭", "┬", "╮", color, truecolor));
        Confirm.Status(BuildPlanHeader(headers, widths, color, truecolor));
        Confirm.Status(BuildBorder(widths, "├", "┼", "┤", color, truecolor));
        foreach (var (src, name, ver) in rows)
        {
            var tint = string.Equals(src, "aur", StringComparison.OrdinalIgnoreCase) ? AurTint : RepoTint;
            Confirm.Status(BuildPlanRow(src, name, ver, widths, tint, color, truecolor));
        }
        Confirm.Status(BuildBorder(widths, "╰", "┴", "╯", color, truecolor));
    }

    private static string BuildPlanHeader(string[] headers, int[] widths, bool color, bool truecolor)
    {
        var sb = new StringBuilder();
        var bar = Paint("│", BorderColor, color, truecolor);
        sb.Append(bar);
        for (var i = 0; i < headers.Length; i++)
        {
            sb.Append(' ');
            sb.Append(Paint(headers[i].PadRight(widths[i]), HeaderColor, color, truecolor));
            sb.Append(' ');
            sb.Append(bar);
        }
        return sb.ToString();
    }

    private static string BuildPlanRow(
        string source,
        string name,
        string version,
        int[] widths,
        (byte r, byte g, byte b, byte idx) sourceTint,
        bool color,
        bool truecolor)
    {
        var sb = new StringBuilder();
        var bar = Paint("│", BorderColor, color, truecolor);
        sb.Append(bar);
        sb.Append(' ');
        sb.Append(Paint(source.PadRight(widths[0]), sourceTint, color, truecolor));
        sb.Append(' ');
        sb.Append(bar);
        sb.Append(' ');
        sb.Append(Paint(name.PadRight(widths[1]), NameColor, color, truecolor));
        sb.Append(' ');
        sb.Append(bar);
        sb.Append(' ');
        sb.Append(Paint(version.PadRight(widths[2]), NewColor, color, truecolor));
        sb.Append(' ');
        sb.Append(bar);
        return sb.ToString();
    }

    public enum ResultStatus { Success, Skipped, Failed }

    /// <summary>
    /// Render a final summary table covering both phases of an upgrade run.
    /// Each row is a logical phase ("Repo upgrades", "AUR upgrades", ...) with
    /// a glyph status, a short detail string, and an optional count.
    /// </summary>
    public static void RenderSummary(IReadOnlyList<(string Phase, ResultStatus Status, string Detail)> rows)
    {
        if (rows.Count == 0) return;
        var truecolor = SupportsTrueColor();
        var color = ColorEnabled();

        string[] headers = { "Result", "Phase", "Detail" };
        var statusCells = rows.Select(r => GlyphFor(r.Status)).ToArray();
        var phaseCells = rows.Select(r => r.Phase).ToArray();
        var detailCells = rows.Select(r => r.Detail).ToArray();
        var widths = new[]
        {
            Math.Max(headers[0].Length, statusCells.Max(s => s.Length)),
            Math.Max(headers[1].Length, phaseCells.Max(s => s.Length)),
            Math.Max(headers[2].Length, detailCells.Length == 0 ? 0 : detailCells.Max(s => s.Length)),
        };

        var hasFailure = rows.Any(r => r.Status == ResultStatus.Failed);
        Confirm.Status(string.Empty);
        Confirm.Status(hasFailure ? "Summary (with failures)" : "Summary");
        Confirm.Status(BuildBorder(widths, "╭", "┬", "╮", color, truecolor));
        Confirm.Status(BuildSummaryHeader(headers, widths, color, truecolor));
        Confirm.Status(BuildBorder(widths, "├", "┼", "┤", color, truecolor));
        foreach (var r in rows)
        {
            Confirm.Status(BuildSummaryRow(r.Status, r.Phase, r.Detail, widths, color, truecolor));
        }
        Confirm.Status(BuildBorder(widths, "╰", "┴", "╯", color, truecolor));
    }

    private static string GlyphFor(ResultStatus s) => s switch
    {
        ResultStatus.Success => "✓",
        ResultStatus.Skipped => "·",
        ResultStatus.Failed => "✗",
        _ => "?",
    };

    private static (byte r, byte g, byte b, byte idx) TintFor(ResultStatus s) => s switch
    {
        ResultStatus.Success => NewColor,
        ResultStatus.Skipped => OldColor,
        ResultStatus.Failed => FailColor,
        _ => OldColor,
    };

    private static readonly (byte r, byte g, byte b, byte idx) FailColor = (0xFF, 0x5F, 0x5F, 203);

    private static string BuildSummaryHeader(
        string[] headers,
        int[] widths,
        bool color,
        bool truecolor)
    {
        var sb = new StringBuilder();
        var bar = Paint("│", BorderColor, color, truecolor);
        sb.Append(bar);
        for (var i = 0; i < headers.Length; i++)
        {
            sb.Append(' ');
            sb.Append(Paint(headers[i].PadRight(widths[i]), HeaderColor, color, truecolor));
            sb.Append(' ');
            sb.Append(bar);
        }
        return sb.ToString();
    }

    private static string BuildSummaryRow(
        ResultStatus status,
        string phase,
        string detail,
        int[] widths,
        bool color,
        bool truecolor)
    {
        var sb = new StringBuilder();
        var bar = Paint("│", BorderColor, color, truecolor);
        var tint = TintFor(status);
        sb.Append(bar);
        sb.Append(' ');
        sb.Append(Paint(GlyphFor(status).PadRight(widths[0]), tint, color, truecolor));
        sb.Append(' ');
        sb.Append(bar);
        sb.Append(' ');
        sb.Append(Paint(phase.PadRight(widths[1]), NameColor, color, truecolor));
        sb.Append(' ');
        sb.Append(bar);
        sb.Append(' ');
        sb.Append(Paint(detail.PadRight(widths[2]), tint, color, truecolor));
        sb.Append(' ');
        sb.Append(bar);
        return sb.ToString();
    }

    private static void RenderTable(
        IReadOnlyList<(string Repo, string Name, string From, string To)> rows,
        (byte r, byte g, byte b, byte idx) repoTint)
    {
        var truecolor = SupportsTrueColor();
        var color = ColorEnabled();

        string[] headers = { "Repo", "Name", "From", "To" };
        var widths = headers.Select(h => h.Length).ToArray();
        foreach (var r in rows)
        {
            widths[0] = Math.Max(widths[0], r.Repo.Length);
            widths[1] = Math.Max(widths[1], r.Name.Length);
            widths[2] = Math.Max(widths[2], r.From.Length);
            widths[3] = Math.Max(widths[3], r.To.Length);
        }

        Confirm.Status(BuildBorder(widths, "╭", "┬", "╮", color, truecolor));
        Confirm.Status(BuildRow(headers, widths, color, truecolor, isHeader: true, repoTint));
        Confirm.Status(BuildBorder(widths, "├", "┼", "┤", color, truecolor));
        foreach (var (repo, name, from, to) in rows)
        {
            Confirm.Status(BuildRow(new[] { repo, name, from, to }, widths, color, truecolor, isHeader: false, repoTint));
        }
        Confirm.Status(BuildBorder(widths, "╰", "┴", "╯", color, truecolor));
    }

    private static string BuildBorder(
        int[] widths,
        string left,
        string mid,
        string right,
        bool color,
        bool truecolor)
    {
        var sb = new StringBuilder();
        sb.Append(Paint(left, BorderColor, color, truecolor));
        for (var i = 0; i < widths.Length; i++)
        {
            sb.Append(Paint(new string('─', widths[i] + 2), BorderColor, color, truecolor));
            sb.Append(Paint(i == widths.Length - 1 ? right : mid, BorderColor, color, truecolor));
        }
        return sb.ToString();
    }

    private static string BuildRow(
        IReadOnlyList<string> cells,
        int[] widths,
        bool color,
        bool truecolor,
        bool isHeader,
        (byte r, byte g, byte b, byte idx) repoTint)
    {
        var sb = new StringBuilder();
        var bar = Paint("│", BorderColor, color, truecolor);
        sb.Append(bar);
        for (var i = 0; i < cells.Count; i++)
        {
            sb.Append(' ');
            var text = cells[i].PadRight(widths[i]);
            var tint = isHeader
                ? HeaderColor
                : i switch
                {
                    0 => repoTint,
                    1 => NameColor,
                    2 => OldColor,
                    3 => NewColor,
                    _ => OldColor,
                };
            sb.Append(Paint(text, tint, color, truecolor));
            sb.Append(' ');
            sb.Append(bar);
        }
        return sb.ToString();
    }

    private static bool TryParse(string line, out string name, out string from, out string to)
    {
        name = from = to = string.Empty;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4) return false;
        if (parts[2] != "->") return false;
        name = parts[0];
        from = parts[1];
        to = parts[3];
        return true;
    }

    private static string Paint(string text, (byte r, byte g, byte b, byte idx) c, bool color, bool truecolor)
    {
        if (!color) return text;
        var open = truecolor
            ? $"\u001b[38;2;{c.r};{c.g};{c.b}m"
            : $"\u001b[38;5;{c.idx}m";
        return open + text + Reset;
    }

    private static bool ColorEnabled() => ColorSupport.StatusColorEnabled();

    private static bool SupportsTrueColor() => ColorSupport.SupportsTrueColor();
}
