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
    private readonly List<TuiWidget> _children = [];

    public TuiStack(TuiOrientation orientation = TuiOrientation.Vertical)
    {
        Orientation = orientation;
    }

    public TuiOrientation Orientation { get; set; }

    /// <summary>Blank cells between children.</summary>
    public int Gap { get; set; }

    /// <summary>
    /// The children, in order. Settable so a stack can be written as a literal.
    /// </summary>
    public IReadOnlyList<TuiWidget> Items
    {
        get => _children;
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            _children.Clear();
            _children.AddRange(value);
        }
    }

    public override IReadOnlyList<TuiWidget> Children => _children;

    /// <summary>Adds a child, at whatever size it asks for. Returns this stack.</summary>
    public TuiStack Add(TuiWidget child)
    {
        ArgumentNullException.ThrowIfNull(child);

        _children.Add(child);
        return this;
    }

    /// <summary>Adds a child at a given size. Returns this stack.</summary>
    /// <remarks>
    /// Sets the size on the child rather than remembering it here, so the two cannot
    /// disagree and a child moved between containers keeps the size it was given.
    /// </remarks>
    public TuiStack Add(TuiWidget child, TuiLength length)
    {
        ArgumentNullException.ThrowIfNull(child);

        child.Size = length;
        _children.Add(child);
        return this;
    }

    protected override TuiSize MeasureCore(TuiConstraints constraints)
    {
        var horizontal = Orientation == TuiOrientation.Horizontal;
        var along = 0;
        var across = 0;

        foreach (var child in _children)
        {
            // A hidden child asks for nothing. Measuring it would answer nothing anyway,
            // but a fixed length is taken from the child rather than measured, so without
            // this a hidden pane written as "20" still claimed its twenty columns.
            if (!child.IsVisible)
            {
                continue;
            }

            var length = child.Size;

            // A fixed length settles one dimension, not both. Reporting the full offered
            // size across the stack's axis made a row containing anything fixed claim
            // every row on the screen: a labelled field is such a row, so a column of them
            // left nothing for the second one onwards.
            //
            // Everything else is measured, star children included. "A share of what is
            // left" is an instruction for arranging; asked how big it would like to be, a
            // stack answers with its content.
            var desired = length.Kind == TuiLengthKind.Fixed
                ? Fix(child, length.Value, horizontal, constraints)
                : child.Measure(constraints);

            along += horizontal ? desired.Width : desired.Height;
            across = Math.Max(across, horizontal ? desired.Height : desired.Width);
        }

        along += Gap * Math.Max(0, _children.Count(child => child.IsVisible) - 1);

        return constraints.Constrain(horizontal
            ? new TuiSize(along, across)
            : new TuiSize(across, along));
    }

    /// <summary>Measures a child that has already been told one of its dimensions.</summary>
    private static TuiSize Fix(TuiWidget child, int fixedLength, bool horizontal, TuiConstraints constraints)
    {
        var offered = horizontal
            ? new TuiConstraints(fixedLength, constraints.MaxHeight)
            : new TuiConstraints(constraints.MaxWidth, fixedLength);

        var measured = child.Measure(offered);

        return horizontal
            ? new TuiSize(fixedLength, measured.Height)
            : new TuiSize(measured.Width, fixedLength);
    }

    protected override void ArrangeCore(TuiRect bounds)
    {
        var horizontal = Orientation == TuiOrientation.Horizontal;
        var total = horizontal ? bounds.Width : bounds.Height;
        var gaps = Gap * Math.Max(0, _children.Count(child => child.IsVisible) - 1);
        var sizes = Distribute(Math.Max(0, total - gaps), horizontal, bounds);

        var offset = 0;

        for (var index = 0; index < _children.Count; index += 1)
        {
            var size = sizes[index];

            _children[index].Arrange(horizontal
                ? new TuiRect(bounds.Left + offset, bounds.Top, size, bounds.Height)
                : new TuiRect(bounds.Left, bounds.Top + offset, bounds.Width, size));

            // A hidden child takes no room, so the ones after it close up rather than
            // leaving a gap where it would have been.
            if (_children[index].IsVisible)
            {
                offset += size + Gap;
            }
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

        // Everything with an answer of its own is settled first, in the order written, and
        // each is clamped to whatever bounds it carries. Only then is the leftover shared,
        // which is what "a share of what is left" has to mean if it is to mean anything.
        for (var index = 0; index < _children.Count; index += 1)
        {
            var child = _children[index];
            // A hidden child is not a child with nothing in it: it takes no row and no
            // share, so the ones beside it close up rather than leaving a gap.
            if (!child.IsVisible)
            {
                continue;
            }

            var length = child.Size;

            switch (length.Kind)
            {
                case TuiLengthKind.Fixed:
                    sizes[index] = Take(length, length.Value, ref remaining);
                    break;

                case TuiLengthKind.Fraction:
                    sizes[index] = Take(length, available * length.Value / length.Divisor, ref remaining);
                    break;

                case TuiLengthKind.Auto:
                    var constraints = horizontal
                        ? new TuiConstraints(Math.Max(0, remaining), bounds.Height)
                        : new TuiConstraints(bounds.Width, Math.Max(0, remaining));
                    var desired = child.Measure(constraints);
                    sizes[index] = Take(length, horizontal ? desired.Width : desired.Height, ref remaining);
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
        var lastUnbounded = -1;

        for (var index = 0; index < _children.Count; index += 1)
        {
            var length = _children[index].Size;

            // Hidden children were left out of the weight, so they must be left out of the
            // sharing too. Paying them anyway ran the total handed out past the share, and
            // the absorber below then subtracted the overspend from the last visible
            // child — which is why a dialog that was the last of its siblings vanished.
            if (length.Kind != TuiLengthKind.Star || !_children[index].IsVisible)
            {
                continue;
            }

            sizes[index] = length.Clamp(share * length.Value / totalWeight);
            handedOut += sizes[index];

            // The rounding error goes to a star child that can absorb it. One pinned by a
            // bound cannot, and giving it the remainder anyway is how a bounded pane ends
            // up one column wider than it asked to be.
            if (length is { Minimum: null, Maximum: null })
            {
                lastUnbounded = index;
            }
        }

        if (lastUnbounded >= 0)
        {
            sizes[lastUnbounded] = Math.Max(0, sizes[lastUnbounded] + share - handedOut);
        }

        return sizes;

        static int Take(TuiLength length, int wanted, ref int remaining)
        {
            var size = Math.Clamp(length.Clamp(wanted), 0, Math.Max(0, remaining));
            remaining -= size;
            return size;
        }
    }

    public override void Draw(TuiSurface surface)
    {
        foreach (var child in _children)
        {
            // Each child draws into its own region and cannot reach outside it.
            DrawChild(child, surface);
        }
    }
}
