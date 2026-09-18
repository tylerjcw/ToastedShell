using Tosh.Tui.Rendering;

namespace Tosh.Tui.Widgets;

/// <summary>Which edge a docked child takes its room from.</summary>
public enum TuiDockSide
{
    /// <summary>Across the top, as tall as it asked to be.</summary>
    Top,

    /// <summary>Across the bottom.</summary>
    Bottom,

    /// <summary>Down the left, as wide as it asked to be.</summary>
    Left,

    /// <summary>Down the right.</summary>
    Right,

    /// <summary>Everything the edges left over.</summary>
    Fill,
}

/// <summary>One docked child and the edge it takes.</summary>
public sealed record TuiDocked(TuiWidget Content, TuiDockSide Side);

/// <summary>
/// Edges first, and whatever is left over to the middle.
/// </summary>
/// <remarks>
/// <para>
/// The shape almost every full-screen application has: a title across the top, a status line
/// across the bottom, a sidebar down one side, and the document filling what remains. A
/// stack can do it only by nesting — a column holding a header, a row holding a sidebar and
/// a body, and a footer — which is three containers describing one idea, and the author has
/// to get the nesting order right to make the sidebar run full height or not
/// (<c>TUI-0002</c>).
/// </para>
/// <para>
/// Order decides the corners, which is the whole of the semantics and the only thing to
/// learn. A child docked to the top before one docked to the left runs the full width and
/// the left one starts below it; written the other way round, the left one runs full height
/// and the top one starts beside it. That is not an accident to be worked around — it is how
/// you say which of the two owns the corner.
/// </para>
/// </remarks>
public sealed class TuiDock : TuiWidget
{
    private readonly List<TuiDocked> _items = [];
    private readonly Dictionary<TuiWidget, TuiRect> _slots = [];

    public TuiDock(IEnumerable<TuiDocked>? items = null)
    {
        if (items is not null)
        {
            _items.AddRange(items);
        }
    }

    /// <summary>The docked children, in the order their edges are taken.</summary>
    public IReadOnlyList<TuiDocked> Items
    {
        get => _items;
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            _items.Clear();
            _items.AddRange(value);
        }
    }

    /// <summary>Docks a child to an edge. Returns this dock.</summary>
    public TuiDock Add(TuiWidget content, TuiDockSide side)
    {
        ArgumentNullException.ThrowIfNull(content);

        _items.Add(new TuiDocked(content, side));

        return this;
    }

    /// <inheritdoc />
    public override IReadOnlyList<TuiWidget> Children
        => [.. _items.Select(item => item.Content)];

    /// <inheritdoc />
    /// <remarks>
    /// A dock fills what it is given. Asking its children how big they would like to be and
    /// adding that up answers a different question — the edges are sized against the space
    /// that is actually there, and there is no arrangement of them that is "the natural size
    /// of a dock".
    /// </remarks>
    protected override TuiSize MeasureCore(TuiConstraints constraints)
        => constraints.Constrain(new TuiSize(constraints.MaxWidth, constraints.MaxHeight));

    /// <inheritdoc />
    protected override void ArrangeCore(TuiRect bounds)
    {
        _slots.Clear();

        var left = bounds.Left;
        var top = bounds.Top;
        var width = bounds.Width;
        var height = bounds.Height;

        // Decided before any edge is laid out, because the filler must not also take an
        // edge's worth of room on the way past. Working it out afterwards left the fallback
        // unreachable: every non-Fill child already had a slot, so "the last child fills"
        // never once happened.
        var filler = Filler();

        foreach (var item in _items)
        {
            if (!item.Content.IsVisible || ReferenceEquals(item.Content, filler))
            {
                continue;
            }

            var wanted = item.Content.Measure(new TuiConstraints(width, height));

            switch (item.Side)
            {
                case TuiDockSide.Top:
                {
                    var taken = Math.Clamp(wanted.Height, 0, height);

                    _slots[item.Content] = new TuiRect(left, top, width, taken);
                    top += taken;
                    height -= taken;
                    break;
                }

                case TuiDockSide.Bottom:
                {
                    var taken = Math.Clamp(wanted.Height, 0, height);

                    _slots[item.Content] = new TuiRect(left, top + height - taken, width, taken);
                    height -= taken;
                    break;
                }

                case TuiDockSide.Left:
                {
                    var taken = Math.Clamp(wanted.Width, 0, width);

                    _slots[item.Content] = new TuiRect(left, top, taken, height);
                    left += taken;
                    width -= taken;
                    break;
                }

                case TuiDockSide.Right:
                {
                    var taken = Math.Clamp(wanted.Width, 0, width);

                    _slots[item.Content] = new TuiRect(left + width - taken, top, taken, height);
                    width -= taken;
                    break;
                }
            }
        }

        foreach (var item in _items)
        {
            if (!item.Content.IsVisible)
            {
                continue;
            }

            if (ReferenceEquals(item.Content, filler))
            {
                var rest = new TuiRect(left, top, Math.Max(0, width), Math.Max(0, height));

                // Measured before it is arranged, like every other child. The edges were
                // measured on the way past and this one was not, so a child that works out
                // its own layout during measure — a grid does, for its tracks — was arranged
                // against numbers it had never computed, and drew nothing at all.
                item.Content.Measure(new TuiConstraints(rest.Width, rest.Height));
                item.Content.Arrange(rest);

                continue;
            }

            item.Content.Arrange(_slots[item.Content]);
        }
    }

    /// <summary>
    /// Which child gets the middle: the one that asked, or the last one written.
    /// </summary>
    /// <remarks>
    /// The last <c>Fill</c> wins where there are several — two fillers is a mistake rather
    /// than a layout, and stacking them would hide it. With none, the last visible child
    /// takes what the edges left, which is what every other container does with its last
    /// child and what <c>DockPanel</c> has meant by <c>LastChildFill</c> for twenty years.
    /// </remarks>
    private TuiWidget? Filler()
    {
        TuiWidget? asked = null;

        foreach (var item in _items.Where(item => item.Content.IsVisible))
        {
            if (item.Side == TuiDockSide.Fill)
            {
                asked = item.Content;
            }
        }

        return asked ?? _items.LastOrDefault(item => item.Content.IsVisible)?.Content;
    }

    /// <inheritdoc />
    public override void Draw(TuiSurface surface)
    {
        foreach (var item in _items)
        {
            DrawChild(item.Content, surface);
        }
    }
}
