using Tosh.Runtime;
using Tosh.Tui.Rendering;

namespace Tosh.Tui.Widgets;

/// <summary>A box drawn around one child, optionally with a title in the top edge.</summary>
/// <remarks>
/// The panels both browsers draw by hand, as a widget. A border is the cheapest thing
/// that makes a screen look composed rather than printed, which is most of what
/// "attractive by default" means in a terminal.
/// </remarks>
public sealed class TuiBorder : TuiWidget
{
    public TuiBorder(TuiWidget? child = null, string? title = null)
    {
        Child = child;
        Title = title;
    }

    public TuiWidget? Child { get; set; }

    /// <summary>Shown in the top edge, when there is room for it.</summary>
    public string? Title { get; set; }

    /// <summary>How the box itself is drawn.</summary>
    public TuiStyle Style { get; set; }

    /// <summary>How the title is drawn.</summary>
    public TuiStyle TitleStyle { get; set; }

    /// <summary>The glyphs to draw the box with.</summary>
    public TuiBorderGlyphs Glyphs { get; set; } = TuiBorderGlyphs.Rounded;


    /// <summary>A script function that supplies the title, re-read on every redraw.</summary>
    /// <remarks>
    /// Assigning one of these is what makes a property live. Setting <see cref="Title"/>
    /// puts a value there once; setting this puts a question there, asked again each time
    /// the screen is drawn.
    /// </remarks>
    public IShellCallable? TitleSource { get; set; }

    public override IReadOnlyList<TuiWidget> Children => Child is null ? [] : [Child];

    public override TuiSize Measure(TuiConstraints constraints)
    {
        var inner = Child?.Measure(constraints.Shrink(2, 2)) ?? new TuiSize(0, 0);
        var titleWidth = Title is null ? 0 : TuiTextMeasure.MeasureWidth(Title) + 4;

        return constraints.Constrain(new TuiSize(
            Math.Max(inner.Width, titleWidth) + 2,
            inner.Height + 2));
    }

    public override void Arrange(TuiRect bounds)
    {
        base.Arrange(bounds);

        // A box two cells high has no inside; the child gets nothing rather than a
        // negative size.
        Child?.Arrange(new TuiRect(
            bounds.Left + 1,
            bounds.Top + 1,
            Math.Max(0, bounds.Width - 2),
            Math.Max(0, bounds.Height - 2)));
    }

    public override void Draw(TuiSurface surface)
    {
        if (surface.Width < 2 || surface.Height < 2)
        {
            return;
        }

        var right = surface.Width - 1;
        var bottom = surface.Height - 1;

        surface.DrawText(0, 0, Glyphs.TopLeft, Style);
        surface.DrawText(right, 0, Glyphs.TopRight, Style);
        surface.DrawText(0, bottom, Glyphs.BottomLeft, Style);
        surface.DrawText(right, bottom, Glyphs.BottomRight, Style);

        for (var column = 1; column < right; column += 1)
        {
            surface.DrawText(column, 0, Glyphs.Horizontal, Style);
            surface.DrawText(column, bottom, Glyphs.Horizontal, Style);
        }

        for (var row = 1; row < bottom; row += 1)
        {
            surface.DrawText(0, row, Glyphs.Vertical, Style);
            surface.DrawText(right, row, Glyphs.Vertical, Style);
        }

        DrawTitle(surface, right);

        if (Child is not null)
        {
            Child.Draw(surface.Clip(new TuiRect(1, 1, Math.Max(0, surface.Width - 2), Math.Max(0, surface.Height - 2))));
        }
    }

    /// <summary>
    /// Writes the title into the top edge, padded so the box reads as a label rather
    /// than as text jammed against a line.
    /// </summary>
    private void DrawTitle(TuiSurface surface, int right)
    {
        if (string.IsNullOrEmpty(Title))
        {
            return;
        }

        // Two cells for the corners, two for the padding either side of the title.
        var available = right - 3;

        if (available <= 0)
        {
            return;
        }

        var text = TuiTextMeasure.Truncate(Title, available);

        if (text.Length == 0)
        {
            return;
        }

        surface.DrawText(1, 0, " ", Style);
        var used = surface.DrawText(2, 0, text, TitleStyle);
        surface.DrawText(2 + used, 0, " ", Style);
    }
}

/// <summary>The glyphs a box is drawn with.</summary>
public readonly record struct TuiBorderGlyphs(
    string TopLeft,
    string TopRight,
    string BottomLeft,
    string BottomRight,
    string Horizontal,
    string Vertical)
{
    /// <summary>Rounded corners, as both browsers draw today.</summary>
    public static TuiBorderGlyphs Rounded => new("╭", "╮", "╰", "╯", "─", "│");

    /// <summary>Square corners.</summary>
    public static TuiBorderGlyphs Square => new("┌", "┐", "└", "┘", "─", "│");

    /// <summary>Heavier lines, for a focused pane.</summary>
    public static TuiBorderGlyphs Heavy => new("┏", "┓", "┗", "┛", "━", "┃");

    /// <summary>Double lines.</summary>
    public static TuiBorderGlyphs Double => new("╔", "╗", "╚", "╝", "═", "║");

    /// <summary>Plain characters, for a terminal that cannot draw the rest.</summary>
    public static TuiBorderGlyphs Ascii => new("+", "+", "+", "+", "-", "|");
}
