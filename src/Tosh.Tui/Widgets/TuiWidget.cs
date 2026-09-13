using Tosh.Tui.Rendering;

namespace Tosh.Tui.Widgets;

/// <summary>How much room a parent is offering a child.</summary>
/// <remarks>
/// Only maximums. A terminal layout never needs a minimum: a widget that wants more room
/// than it is given is clipped, and one that wants less is simply smaller.
/// </remarks>
public readonly record struct TuiConstraints(int MaxWidth, int MaxHeight)
{
    public static TuiConstraints Unbounded => new(int.MaxValue, int.MaxValue);

    public static TuiConstraints From(TuiSize size) => new(size.Width, size.Height);

    /// <summary>The same constraints with less room, floored at nothing.</summary>
    public TuiConstraints Shrink(int columns, int rows) => new(
        MaxWidth == int.MaxValue ? int.MaxValue : Math.Max(0, MaxWidth - columns),
        MaxHeight == int.MaxValue ? int.MaxValue : Math.Max(0, MaxHeight - rows));

    /// <summary>Clamps a desired size to what is on offer.</summary>
    public TuiSize Constrain(TuiSize size) => new(
        Math.Min(size.Width, MaxWidth),
        Math.Min(size.Height, MaxHeight));
}

/// <summary>How a container divides space between its children.</summary>
/// <remarks>
/// Three kinds, which between them cover what terminal layouts actually ask for: a
/// status bar that is one row (<see cref="Fixed"/>), a detail pane that takes what is
/// left (<see cref="Star"/>), and a label that is as wide as its text
/// (<see cref="Auto"/>).
/// </remarks>
public readonly record struct TuiLength
{
    private TuiLength(int value, TuiLengthKind kind)
    {
        Value = value;
        Kind = kind;
    }

    public int Value { get; }

    public TuiLengthKind Kind { get; }

    /// <summary>As big as the child asks to be.</summary>
    public static TuiLength Auto => new(0, TuiLengthKind.Auto);

    /// <summary>Exactly this many cells.</summary>
    public static TuiLength Fixed(int cells) => new(Math.Max(0, cells), TuiLengthKind.Fixed);

    /// <summary>A share of whatever is left over, split by weight.</summary>
    public static TuiLength Star(int weight = 1) => new(Math.Max(1, weight), TuiLengthKind.Star);

    /// <summary>
    /// Reads a length written the way a layout author writes one.
    /// </summary>
    /// <remarks>
    /// <c>"*"</c> and <c>"2*"</c> are the spellings a layout uses, <c>"auto"</c> means as
    /// big as the content, and a bare number is cells. Anything unrecognised is auto,
    /// which is the safe answer: a widget sized to its content is always drawable.
    /// </remarks>
    public static TuiLength Parse(string? text)
    {
        var trimmed = text?.Trim() ?? string.Empty;

        if (trimmed.Length == 0 || trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return Auto;
        }

        if (trimmed.EndsWith('*'))
        {
            var weight = trimmed[..^1].Trim();
            return Star(weight.Length == 0 ? 1 : int.TryParse(weight, out var parsed) ? parsed : 1);
        }

        return int.TryParse(trimmed, out var cells) ? Fixed(cells) : Auto;
    }

    /// <summary>So a layout can be written as <c>Size = "2*"</c>.</summary>
    public static implicit operator TuiLength(string text) => Parse(text);

    /// <summary>So a layout can be written as <c>Size = 12</c>.</summary>
    public static implicit operator TuiLength(int cells) => Fixed(cells);

    public override string ToString() => Kind switch
    {
        TuiLengthKind.Auto => "auto",
        TuiLengthKind.Fixed => Value.ToString(),
        _ => Value == 1 ? "*" : $"{Value}*",
    };
}

public enum TuiLengthKind
{
    Auto,
    Fixed,
    Star,
}

/// <summary>
/// Something that can size itself, be placed, draw itself, and answer for input.
/// </summary>
/// <remarks>
/// <para>
/// The framework had widget <em>state</em> — selection, scroll, editing — and no widget:
/// not one of those types could draw, so every screen drew for them and the set of
/// widgets a script could use was a closed list of six (<c>TUI-0002</c>).
/// </para>
/// <para>
/// Measuring and arranging are separate passes because that is what makes sizes
/// compose. A single pass can do "half the width" and "two rows", which is what the old
/// four fixed layouts did; it cannot do "as tall as its content", and intrinsic sizing
/// is what a form, a fitted column or a dialog sized to its message needs in order to
/// work without arithmetic at the call site.
/// </para>
/// </remarks>
public abstract class TuiWidget
{
    /// <summary>
    /// A name for this widget, used to address it and to key its value in a result.
    /// </summary>
    /// <remarks>
    /// Optional. Most widgets in a tree never need naming — a border, a label, a row —
    /// and a declarative screen should not force ids on things nobody refers to.
    /// </remarks>
    public string? Id { get; set; }

    /// <summary>
    /// What this widget holds, for widgets that hold something.
    /// </summary>
    /// <remarks>
    /// A text field holds its text, a list its selection. Everything else holds nothing,
    /// which is why the default is null rather than an abstract member: a border has no
    /// value and should not have to say so.
    /// </remarks>
    public virtual object? Value => null;

    /// <summary>
    /// How much room this widget asks its parent for.
    /// </summary>
    /// <remarks>
    /// On the child rather than held by the parent, so a widget can be built complete and
    /// handed over — which is what makes a tree writable as a literal, or assembled a
    /// line at a time, without the parent having to be told about each child twice.
    ///
    /// Auto by default: a widget takes what it needs, and filling the space is something
    /// a layout asks for explicitly. The opposite default spreads a column of form fields
    /// evenly down the pane, each with blank rows under it.
    /// </remarks>
    public TuiLength Size { get; set; } = TuiLength.Auto;

    /// <summary>Where this widget was last placed, in its parent's coordinates.</summary>
    public TuiRect Bounds { get; private set; }

    /// <summary>Whether this widget can hold keyboard focus.</summary>
    public virtual bool IsFocusable => false;

    /// <summary>
    /// Whether this widget currently holds the keyboard. Set by <see cref="TuiFocus"/>.
    /// </summary>
    /// <remarks>
    /// A widget needs this to draw itself — a focused pane with a heavier border, a
    /// selected button with a marker — and should not otherwise act on it. What happens
    /// to an event is routing's business, not the widget's.
    /// </remarks>
    public bool IsFocused { get; internal set; }

    /// <summary>The widget's children, outermost first.</summary>
    public virtual IReadOnlyList<TuiWidget> Children => [];

    /// <summary>How much room this widget would like, given what is on offer.</summary>
    public abstract TuiSize Measure(TuiConstraints constraints);

    /// <summary>
    /// Accepts a final position and size, and places any children within it.
    /// </summary>
    /// <remarks>
    /// The size given is not always the size asked for. A widget must cope with less.
    /// </remarks>
    public virtual void Arrange(TuiRect bounds) => Bounds = bounds;

    /// <summary>Draws onto a surface already clipped to this widget's bounds.</summary>
    public abstract void Draw(TuiSurface surface);

    /// <summary>
    /// Handles an input event, returning whether it was consumed.
    /// </summary>
    /// <remarks>
    /// Returning false lets the event continue outwards to an ancestor, which is how a
    /// screen-level shortcut coexists with a text field that needs the same key. A
    /// focused text input consuming every printable character is what stops <c>q</c>
    /// quitting the browser while someone is typing a search (<c>TOSH-0011</c>).
    /// </remarks>
    public virtual bool OnInput(TuiInputEvent input) => false;

    /// <summary>The deepest widget covering a point, or null when none does.</summary>
    /// <remarks>
    /// Hit testing walks the arrangement instead of each screen remembering where it put
    /// things in fields it updates during rendering.
    /// </remarks>
    public virtual TuiWidget? HitTest(int column, int row)
    {
        if (!Bounds.Contains(column, row))
        {
            return null;
        }

        // Later children draw over earlier ones, so they are asked first.
        for (var index = Children.Count - 1; index >= 0; index -= 1)
        {
            if (Children[index].HitTest(column, row) is { } hit)
            {
                return hit;
            }
        }

        return this;
    }
}
