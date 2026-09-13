using Tosh.Tui.Rendering;

namespace Tosh.Tui.Widgets;

/// <summary>Which way a stack lays its children out.</summary>
public enum TuiOrientation
{
    /// <summary>Left to right.</summary>
    Horizontal,

    /// <summary>Top to bottom.</summary>
    Vertical,
}

/// <summary>
/// Lays children out in a line, each taking a fixed size, its natural size, or a share
/// of what is left.
/// </summary>
/// <remarks>
/// This one container replaces the four fixed arrangements the TUI had — single, split
/// horizontal, split vertical and stacked — and does what none of them could: two panes
/// side by side with a status bar under both, which previously meant computing
/// rectangles by hand.
/// </remarks>
public sealed class TuiStack : TuiWidget
{
    private readonly List<(TuiWidget Widget, TuiLength Length)> _children = [];

    public TuiStack(TuiOrientation orientation = TuiOrientation.Vertical)
    {
        Orientation = orientation;
    }

    public TuiOrientation Orientation { get; set; }

    /// <summary>Blank cells between children.</summary>
    public int Gap { get; set; }

    public override IReadOnlyList<TuiWidget> Children => _children.Select(entry => entry.Widget).ToArray();

    /// <summary>Adds a child, sized by <paramref name="length"/>. Returns this stack.</summary>
    public TuiStack Add(TuiWidget child, TuiLength length = default)
    {
        ArgumentNullException.ThrowIfNull(child);

        // The default of a record struct is Auto, which is the right default anyway: a
        // child that has not been told how big to be is as big as it asks.
        _children.Add((child, length));
        return this;
    }

    public override TuiSize Measure(TuiConstraints constraints)
    {
        var horizontal = Orientation == TuiOrientation.Horizontal;
        var along = 0;
        var across = 0;

        foreach (var (child, length) in _children)
        {
            var desired = length.Kind == TuiLengthKind.Fixed
                ? new TuiSize(
                    horizontal ? length.Value : constraints.MaxWidth,
                    horizontal ? constraints.MaxHeight : length.Value)
                : child.Measure(constraints);

            along += horizontal ? desired.Width : desired.Height;
            across = Math.Max(across, horizontal ? desired.Height : desired.Width);
        }

        along += Gap * Math.Max(0, _children.Count - 1);

        return constraints.Constrain(horizontal
            ? new TuiSize(along, across)
            : new TuiSize(across, along));
    }

    public override void Arrange(TuiRect bounds)
    {
        base.Arrange(bounds);

        var horizontal = Orientation == TuiOrientation.Horizontal;
        var total = horizontal ? bounds.Width : bounds.Height;
        var gaps = Gap * Math.Max(0, _children.Count - 1);
        var sizes = Distribute(Math.Max(0, total - gaps), horizontal, bounds);

        var offset = 0;

        for (var index = 0; index < _children.Count; index += 1)
        {
            var size = sizes[index];

            _children[index].Widget.Arrange(horizontal
                ? new TuiRect(bounds.Left + offset, bounds.Top, size, bounds.Height)
                : new TuiRect(bounds.Left, bounds.Top + offset, bounds.Width, size));

            offset += size + Gap;
        }
    }

    /// <summary>
    /// Works out each child's size along the stack's axis.
    /// </summary>
    /// <remarks>
    /// Fixed and auto children are satisfied first, then whatever is left is split
    /// between the star children by weight — so "this pane is 30 columns, that one takes
    /// the rest" needs no arithmetic from the caller. The last star child absorbs the
    /// rounding error rather than leaving a blank column at the edge.
    /// </remarks>
    private int[] Distribute(int available, bool horizontal, TuiRect bounds)
    {
        var sizes = new int[_children.Count];
        var remaining = available;
        var totalWeight = 0;

        for (var index = 0; index < _children.Count; index += 1)
        {
            var (child, length) = _children[index];

            switch (length.Kind)
            {
                case TuiLengthKind.Fixed:
                    sizes[index] = Math.Min(length.Value, Math.Max(0, remaining));
                    remaining -= sizes[index];
                    break;

                case TuiLengthKind.Auto:
                    var constraints = horizontal
                        ? new TuiConstraints(Math.Max(0, remaining), bounds.Height)
                        : new TuiConstraints(bounds.Width, Math.Max(0, remaining));
                    var desired = child.Measure(constraints);
                    sizes[index] = Math.Min(horizontal ? desired.Width : desired.Height, Math.Max(0, remaining));
                    remaining -= sizes[index];
                    break;

                case TuiLengthKind.Star:
                    totalWeight += length.Value;
                    break;
            }
        }

        if (totalWeight == 0)
        {
            return sizes;
        }

        var share = Math.Max(0, remaining);
        var handedOut = 0;
        var lastStar = -1;

        for (var index = 0; index < _children.Count; index += 1)
        {
            if (_children[index].Length.Kind != TuiLengthKind.Star)
            {
                continue;
            }

            sizes[index] = share * _children[index].Length.Value / totalWeight;
            handedOut += sizes[index];
            lastStar = index;
        }

        if (lastStar >= 0)
        {
            sizes[lastStar] += share - handedOut;
        }

        return sizes;
    }

    public override void Draw(TuiSurface surface)
    {
        foreach (var (child, _) in _children)
        {
            // Each child draws into its own region and cannot reach outside it.
            child.Draw(surface.Clip(child.Bounds.Offset(-Bounds.Left, -Bounds.Top)));
        }
    }
}
