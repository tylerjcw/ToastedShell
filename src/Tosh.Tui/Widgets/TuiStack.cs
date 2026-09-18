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
    /// <para>
    /// Fixed and auto children are satisfied first, then whatever is left is split
    /// between the star children by weight — so "this pane is 30 columns, that one takes
    /// the rest" needs no arithmetic from the caller. The last star child absorbs the
    /// rounding error rather than leaving a blank column at the edge.
    /// </para>
    /// <para>
    /// When they do not all fit, everyone gives up the same proportion of what they asked
    /// for above whatever minimum they declared. Satisfying them in written order instead
    /// meant the order decided who got <em>nothing</em>: one long auto child filled the row
    /// and every later sibling was arranged at zero width, so a status bar whose path grew
    /// past the terminal lost the position indicator at the other end of it entirely,
    /// without a mark to say anything had been cut.
    /// </para>
    /// </remarks>
    /// <summary>How many cells each child gets along the stack's own axis.</summary>
    /// <remarks>
    /// The arithmetic is <see cref="TuiTracks"/>, shared with the grid. What is left here is
    /// the one thing a stack knows that a track does not: how to ask a child its natural
    /// size, which means measuring it against the whole line in the cross direction.
    /// </remarks>
    private int[] Distribute(int available, bool horizontal, TuiRect bounds)
        => TuiTracks.Allocate(
            [.. _children.Select(child => child.Size)],
            [.. _children.Select(child => child.IsVisible)],
            available,
            index =>
            {
                var constraints = horizontal
                    ? new TuiConstraints(Math.Max(0, available), bounds.Height)
                    : new TuiConstraints(bounds.Width, Math.Max(0, available));

                var desired = _children[index].Measure(constraints);

                return horizontal ? desired.Width : desired.Height;
            });

    public override void Draw(TuiSurface surface)
    {
        foreach (var child in _children)
        {
            // Each child draws into its own region and cannot reach outside it.
            DrawChild(child, surface);
        }
    }
}
