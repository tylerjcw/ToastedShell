namespace Tosh.Core;

/// <summary>
/// Pre-computed table layout and formatted cells for interactive inline rendering.
/// Built by <see cref="DisplayEngine.BuildInlineTablePlan"/> using the same column resolution,
/// cell formatting, and width-fitting logic as the normal REPL table renderer.
/// </summary>
public sealed class InlineTablePlan
{
    internal InlineTablePlan(
        IReadOnlyList<InlineTableColumn> columns,
        IReadOnlyList<string[]> rows,
        ToshTableBoxStyle boxStyle,
        ToshTableThemeConfig theme)
    {
        Columns = columns;
        Rows = rows;
        BoxStyle = boxStyle;
        Theme = theme;
    }

    /// <summary>Visible columns with header text, width, and alignment.</summary>
    public IReadOnlyList<InlineTableColumn> Columns { get; }

    /// <summary>Pre-formatted cell values per row. Each string[] is indexed by column ordinal.</summary>
    public IReadOnlyList<string[]> Rows { get; }

    /// <summary>Box-drawing style to use.</summary>
    public ToshTableBoxStyle BoxStyle { get; }

    /// <summary>Theme config for border/header/index styling.</summary>
    public ToshTableThemeConfig Theme { get; }

    /// <summary>Whether table data is available (items had renderable columns).</summary>
    public bool HasColumns => Columns.Count > 0;

    /// <summary>Total display width of the table including all borders and padding.</summary>
    public int TotalWidth => Columns.Count == 0 ? 0 : Columns.Sum(c => c.Width + 2) + Columns.Count + 1;

    /// <summary>Get box-drawing characters for the given style.</summary>
    public static BoxChars GetBoxCharacters(ToshTableBoxStyle style) => TerminalGlyphs.ResolveBoxStyle(style) switch
    {
        ToshTableBoxStyle.Square => new('┌', '┬', '┐', '├', '┼', '┤', '└', '┴', '┘', '│', '─'),
        ToshTableBoxStyle.Heavy => new('┏', '┳', '┓', '┣', '╋', '┫', '┗', '┻', '┛', '┃', '━'),
        ToshTableBoxStyle.Ascii => new('+', '+', '+', '+', '+', '+', '+', '+', '+', '|', '-'),
        ToshTableBoxStyle.Double => new('╔', '╦', '╗', '╠', '╬', '╣', '╚', '╩', '╝', '║', '═'),
        _ => new('╭', '┬', '╮', '├', '┼', '┤', '╰', '┴', '╯', '│', '─'),
    };

    /// <summary>Clip cell text to a maximum display width, adding ellipsis if truncated.</summary>
    public static string ClipCell(string value, int width)
    {
        if (width <= 0) return string.Empty;
        // Flatten multi-line values into a single line for inline table rendering
        if (value.Contains('\n'))
            value = value.ReplaceLineEndings(" ");
        if (StyledText.GetVisibleLength(value) <= width) return value;
        var plain = StyledText.StripAnsi(value);
        return width == 1 ? plain[..1] : $"{plain[..Math.Min(width - 1, plain.Length)]}…";
    }

    /// <summary>Right-pad cell text to fill the given width.</summary>
    public static string PadRight(string value, int width)
    {
        var padding = Math.Max(0, width - StyledText.GetVisibleLength(value));
        return padding == 0 ? value : $"{value}{new string(' ', padding)}";
    }

    /// <summary>Left-pad cell text to fill the given width.</summary>
    public static string PadLeft(string value, int width)
    {
        var padding = Math.Max(0, width - StyledText.GetVisibleLength(value));
        return padding == 0 ? value : $"{new string(' ', padding)}{value}";
    }

    /// <summary>Center-pad cell text within the given width.</summary>
    public static string PadCenter(string value, int width)
    {
        var extra = Math.Max(0, width - StyledText.GetVisibleLength(value));
        var left = extra / 2;
        var right = extra - left;
        return $"{new string(' ', left)}{value}{new string(' ', right)}";
    }

    /// <summary>Build a horizontal border line from the given column widths and box characters.</summary>
    public static string BuildBorder(IReadOnlyList<int> columnWidths, char left, char center, char right, char horizontal)
    {
        return $"{left}{string.Join(center, columnWidths.Select(w => new string(horizontal, w + 2)))}{right}";
    }

    /// <summary>Box-drawing characters for table rendering.</summary>
    public readonly record struct BoxChars(
        char TopLeft, char TopMiddle, char TopRight,
        char MiddleLeft, char MiddleMiddle, char MiddleRight,
        char BottomLeft, char BottomMiddle, char BottomRight,
        char Vertical, char Horizontal);
}

/// <summary>A single visible column in an inline table plan.</summary>
public sealed record InlineTableColumn(
    string Header,
    int Width,
    DisplayTableAlignment Alignment,
    bool UseHeaderTheme,
    bool UseIndexTheme);
