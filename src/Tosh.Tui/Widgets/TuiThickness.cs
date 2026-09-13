namespace Tosh.Tui.Widgets;

/// <summary>Blank cells on each side of something.</summary>
/// <remarks>
/// <para>
/// Written the way CSS writes it, because that is the spelling most readers already know:
/// one number for all four sides, two for vertical and horizontal, four for top, right,
/// bottom and left in that order.
/// </para>
/// </remarks>
public readonly record struct TuiThickness(int Left, int Top, int Right, int Bottom)
{
    /// <summary>Nothing on any side.</summary>
    public static readonly TuiThickness None = default;

    /// <summary>The same on all four sides.</summary>
    public static TuiThickness All(int cells) => new(cells, cells, cells, cells);

    /// <summary>Vertical and horizontal.</summary>
    public static TuiThickness Symmetric(int horizontal, int vertical)
        => new(horizontal, vertical, horizontal, vertical);

    /// <summary>How many columns this takes.</summary>
    public int Horizontal => Math.Max(0, Left) + Math.Max(0, Right);

    /// <summary>How many rows this takes.</summary>
    public int Vertical => Math.Max(0, Top) + Math.Max(0, Bottom);

    /// <summary>Whether it takes nothing at all.</summary>
    public bool IsEmpty => Horizontal == 0 && Vertical == 0;

    /// <summary>The rectangle left after taking this off each side.</summary>
    public TuiRect Deflate(TuiRect bounds) => new(
        bounds.Left + Math.Max(0, Left),
        bounds.Top + Math.Max(0, Top),
        Math.Max(0, bounds.Width - Horizontal),
        Math.Max(0, bounds.Height - Vertical));

    /// <summary>
    /// Reads a thickness written the way a layout author writes one.
    /// </summary>
    /// <remarks>
    /// <c>"1"</c>, <c>"1 2"</c> or <c>"1 2 3 4"</c>. Anything unrecognised is nothing,
    /// which is the safe answer: a widget with no padding is always drawable.
    /// </remarks>
    public static TuiThickness Parse(string? text)
    {
        var parts = (text ?? string.Empty)
            .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, out var cells) ? Math.Max(0, cells) : 0)
            .ToArray();

        return parts.Length switch
        {
            1 => All(parts[0]),
            2 => Symmetric(parts[1], parts[0]),
            4 => new TuiThickness(parts[3], parts[0], parts[1], parts[2]),
            _ => None,
        };
    }

    /// <summary>So a layout can be written as <c>Padding = "1 2"</c>.</summary>
    public static implicit operator TuiThickness(string text) => Parse(text);

    /// <summary>So a layout can be written as <c>Padding = 1</c>.</summary>
    public static implicit operator TuiThickness(int cells) => All(cells);

    public override string ToString()
        => Left == Right && Top == Bottom
            ? Left == Top ? Left.ToString() : $"{Top} {Left}"
            : $"{Top} {Right} {Bottom} {Left}";
}

/// <summary>Where something sits when it is shorter than the room it was given.</summary>
public enum TuiVerticalAlignment
{
    Top,
    Middle,
    Bottom,
    /// <summary>Take the whole height, which is what almost everything does.</summary>
    Stretch,
}

/// <summary>Where something sits when it is narrower than the room it was given.</summary>
public enum TuiHorizontalAlignment
{
    Left,
    Center,
    Right,
    /// <summary>Take the whole width, which is what almost everything does.</summary>
    Stretch,
}
