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
/// <para>
/// Four kinds, which between them cover what terminal layouts ask for: a status bar that
/// is one row (<see cref="Fixed"/>), a label as wide as its text (<see cref="Auto"/>), a
/// detail pane taking what is left (<see cref="Star"/>), and a sidebar that is a third of
/// the window (<see cref="Percent"/>).
/// </para>
/// <para>
/// Any of them can be bounded. "A third of the width, but never less than 24 and never
/// more than 40" is one length rather than a <c>Math.Clamp</c> at the call site — which
/// is what the help browser had, and which stopped the pane being a third of anything the
/// moment the terminal was resized past either bound (<c>TUI-0019</c>).
/// </para>
/// </remarks>
public readonly record struct TuiLength
{
    private TuiLength(int value, TuiLengthKind kind, int divisor = 1, int? minimum = null, int? maximum = null)
    {
        Value = value;
        Kind = kind;
        Divisor = Math.Max(1, divisor);
        Minimum = minimum;
        Maximum = maximum;
    }

    public int Value { get; }

    public TuiLengthKind Kind { get; }

    /// <summary>What <see cref="Value"/> is out of, for a <see cref="TuiLengthKind.Fraction"/>.</summary>
    public int Divisor { get; }

    /// <summary>The fewest cells this may take, whatever its kind works out to.</summary>
    public int? Minimum { get; }

    /// <summary>The most cells this may take.</summary>
    public int? Maximum { get; }

    /// <summary>As big as the child asks to be.</summary>
    public static TuiLength Auto => new(0, TuiLengthKind.Auto);

    /// <summary>Exactly this many cells.</summary>
    public static TuiLength Fixed(int cells) => new(Math.Max(0, cells), TuiLengthKind.Fixed);

    /// <summary>A share of whatever is left over, split by weight.</summary>
    public static TuiLength Star(int weight = 1) => new(Math.Max(1, weight), TuiLengthKind.Star);

    /// <summary>
    /// A share of the whole container, rather than of what is left of it.
    /// </summary>
    /// <remarks>
    /// The difference from <see cref="Star"/> is what it is measured against. Two star
    /// children split the leftovers between them; two 30% children take 30% each of
    /// everything, and whatever is left goes to the star children beside them.
    /// </remarks>
    public static TuiLength Percent(int percent) => Ratio(Math.Clamp(percent, 0, 100), 100);

    /// <summary>
    /// A share of the whole container, written as the fraction it actually is.
    /// </summary>
    /// <remarks>
    /// A third is not 33%. Rounding 118 columns by 33/100 gives 38 and by 1/3 gives 39, and
    /// the author who wrote <c>width / 3</c> meant the second. Percentages are the readable
    /// spelling when the number is round; this is the exact one when it is not.
    /// </remarks>
    public static TuiLength Ratio(int numerator, int denominator)
        => new(Math.Max(0, numerator), TuiLengthKind.Fraction, denominator);

    /// <summary>The same length, but never smaller than this.</summary>
    public TuiLength AtLeast(int cells) => new(Value, Kind, Divisor, Math.Max(0, cells), Maximum);

    /// <summary>The same length, but never larger than this.</summary>
    public TuiLength AtMost(int cells) => new(Value, Kind, Divisor, Minimum, Math.Max(0, cells));

    /// <summary>The same length, bounded at both ends.</summary>
    public TuiLength Between(int minimum, int maximum) => AtLeast(minimum).AtMost(maximum);

    /// <summary>Applies whatever bounds were asked for.</summary>
    public int Clamp(int cells)
    {
        if (Minimum is { } minimum)
        {
            cells = Math.Max(cells, minimum);
        }

        if (Maximum is { } maximum)
        {
            cells = Math.Min(cells, maximum);
        }

        return Math.Max(0, cells);
    }

    /// <summary>
    /// Reads a length written the way a layout author writes one.
    /// </summary>
    /// <remarks>
    /// <c>"*"</c> and <c>"2*"</c> are the spellings a layout uses, <c>"33%"</c> is a share
    /// of the container, <c>"auto"</c> means as big as the content, and a bare number is
    /// cells. Bounds follow after a space as a range — <c>"33% 24..40"</c>, <c>"* ..40"</c>,
    /// <c>"auto 10.."</c> — because that is how a range reads everywhere else in the
    /// language. Anything unrecognised is auto, which is the safe answer: a widget sized to
    /// its content is always drawable.
    /// </remarks>
    public static TuiLength Parse(string? text)
    {
        var trimmed = text?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            return Auto;
        }

        var space = trimmed.IndexOf(' ', StringComparison.Ordinal);
        var bounds = space < 0 ? string.Empty : trimmed[(space + 1)..].Trim();
        var head = space < 0 ? trimmed : trimmed[..space];

        var length = ParseKind(head);

        if (bounds.Length == 0)
        {
            return length;
        }

        var range = bounds.Split("..", StringSplitOptions.TrimEntries);

        if (range.Length == 2)
        {
            if (int.TryParse(range[0], out var minimum))
            {
                length = length.AtLeast(minimum);
            }

            if (int.TryParse(range[1], out var maximum))
            {
                length = length.AtMost(maximum);
            }
        }

        return length;
    }

    private static TuiLength ParseKind(string text)
    {
        if (text.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return Auto;
        }

        if (text.EndsWith('*'))
        {
            var weight = text[..^1].Trim();
            return Star(weight.Length == 0 ? 1 : int.TryParse(weight, out var parsed) ? parsed : 1);
        }

        if (text.EndsWith('%'))
        {
            return int.TryParse(text[..^1].Trim(), out var percent) ? Percent(percent) : Auto;
        }

        if (text.Split('/', StringSplitOptions.TrimEntries) is [var top, var bottom] &&
            int.TryParse(top, out var numerator) &&
            int.TryParse(bottom, out var denominator) &&
            denominator > 0)
        {
            return Ratio(numerator, denominator);
        }

        return int.TryParse(text, out var cells) ? Fixed(cells) : Auto;
    }

    /// <summary>So a layout can be written as <c>Size = "2*"</c>.</summary>
    public static implicit operator TuiLength(string text) => Parse(text);

    /// <summary>So a layout can be written as <c>Size = 12</c>.</summary>
    public static implicit operator TuiLength(int cells) => Fixed(cells);

    public override string ToString()
    {
        var head = Kind switch
        {
            TuiLengthKind.Auto => "auto",
            TuiLengthKind.Fixed => Value.ToString(),
            TuiLengthKind.Fraction => Divisor == 100 ? $"{Value}%" : $"{Value}/{Divisor}",
            _ => Value == 1 ? "*" : $"{Value}*",
        };

        return (Minimum, Maximum) switch
        {
            (null, null) => head,
            var (minimum, maximum) => $"{head} {minimum}..{maximum}",
        };
    }
}

public enum TuiLengthKind
{
    Auto,
    Fixed,
    Star,

    /// <summary>A share of the whole container: a percentage, or an exact ratio.</summary>
    Fraction,
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

    /// <summary>
    /// The children the keyboard is allowed to reach, when that is fewer than all of them.
    /// </summary>
    /// <remarks>
    /// A focus scope. Almost every container answers with its children, because almost
    /// every container shows all of them at once; a <see cref="TuiOverlay"/> with a dialog
    /// up answers with the dialog alone, which is what stops Tab walking into a form the
    /// reader cannot currently see.
    /// </remarks>
    public virtual IReadOnlyList<TuiWidget> FocusChildren => Children;

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
