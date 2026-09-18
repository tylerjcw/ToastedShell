using Tosh.Tui.Rendering;

namespace Tosh.Tui.Widgets;

/// <summary>One thing in a grid, and which cells it occupies.</summary>
/// <param name="Row">The row it starts in, counting from zero.</param>
/// <param name="Column">The column it starts in.</param>
/// <param name="RowSpan">How many rows it covers.</param>
/// <param name="ColumnSpan">How many columns it covers.</param>
public sealed record TuiGridItem(
    TuiWidget Content,
    int Row = 0,
    int Column = 0,
    int RowSpan = 1,
    int ColumnSpan = 1);

/// <summary>
/// Children laid out in rows and columns that line up across the whole grid.
/// </summary>
/// <remarks>
/// <para>
/// A row of columns is a stack of stacks until the moment two rows need the same column
/// widths — a label beside a value, repeated down a pane, where every label column has to
/// agree. Nested stacks cannot do that: each one divides its own line and knows nothing
/// about its neighbours, so the author ends up computing widths by hand, which is the thing
/// the layout system exists to stop (<c>TUI-0002</c>).
/// </para>
/// <para>
/// Tracks are sized by <see cref="TuiTracks"/>, the same arithmetic a stack uses, so
/// <c>auto</c>, <c>*</c>, <c>2*</c>, <c>25%</c>, <c>1/3</c> and bounds like <c>auto 12..40</c>
/// all mean here exactly what they mean there. Tracks are separated by commas, because a
/// length already spends the space on its own bounds.
/// </para>
/// </remarks>
public sealed class TuiGrid : TuiWidget
{
    private readonly List<TuiGridItem> _items = [];
    private int[] _columnSizes = [];
    private int[] _rowSizes = [];

    public TuiGrid(string? columns = null, string? rows = null)
    {
        Columns = Parse(columns);
        Rows = Parse(rows);
    }

    /// <summary>
    /// What each column asks for, written the way a size is written anywhere else.
    /// </summary>
    /// <remarks>
    /// Columns nobody declared are <c>*</c>: a grid whose children mention column three is a
    /// grid with four columns, and making the author say so twice is a way to get it wrong
    /// once.
    /// </remarks>
    public IReadOnlyList<TuiLength> Columns { get; set; }

    /// <summary>What each row asks for. Undeclared rows are <c>auto</c>.</summary>
    /// <remarks>
    /// Rows default to <c>auto</c> where columns default to <c>*</c>, because that is what
    /// each is usually for: a column divides the width it was given, and a row is as tall as
    /// what is in it. A grid of rows that each took an equal share of the height would put
    /// three blank lines under every label.
    /// </remarks>
    public IReadOnlyList<TuiLength> Rows { get; set; }

    /// <summary>Blank columns between one column and the next.</summary>
    public int ColumnGap { get; set; } = 1;

    /// <summary>Blank rows between one row and the next.</summary>
    public int RowGap { get; set; }

    /// <summary>Everything in the grid. Settable so a grid can be written as a literal.</summary>
    public IReadOnlyList<TuiGridItem> Items
    {
        get => _items;
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            _items.Clear();
            _items.AddRange(value);
        }
    }

    /// <summary>Puts a widget in a cell. Returns this grid.</summary>
    public TuiGrid Add(TuiWidget content, int row, int column, int rowSpan = 1, int columnSpan = 1)
    {
        ArgumentNullException.ThrowIfNull(content);

        _items.Add(new TuiGridItem(
            content,
            Math.Max(0, row),
            Math.Max(0, column),
            Math.Max(1, rowSpan),
            Math.Max(1, columnSpan)));

        return this;
    }

    /// <inheritdoc />
    public override IReadOnlyList<TuiWidget> Children
        => [.. _items.Select(item => item.Content)];

    /// <summary>How many columns there are: what was declared, or what the children use.</summary>
    public int ColumnCount
        => Math.Max(Columns.Count, _items.Count == 0 ? 0 : _items.Max(item => item.Column + item.ColumnSpan));

    /// <summary>How many rows there are.</summary>
    public int RowCount
        => Math.Max(Rows.Count, _items.Count == 0 ? 0 : _items.Max(item => item.Row + item.RowSpan));

    /// <inheritdoc />
    protected override TuiSize MeasureCore(TuiConstraints constraints)
    {
        _columnSizes = Allocate(
            Track(Columns, ColumnCount, TuiLength.Star()),
            constraints.MaxWidth,
            ColumnGap,
            horizontal: true,
            constraints);

        _rowSizes = Allocate(
            Track(Rows, RowCount, TuiLength.Auto),
            constraints.MaxHeight,
            RowGap,
            horizontal: false,
            constraints);

        return constraints.Constrain(new TuiSize(
            Total(_columnSizes, ColumnGap),
            Total(_rowSizes, RowGap)));
    }

    /// <inheritdoc />
    protected override void ArrangeCore(TuiRect bounds)
    {
        foreach (var item in _items)
        {
            if (!item.Content.IsVisible)
            {
                continue;
            }

            item.Content.Arrange(new TuiRect(
                bounds.Left + Offset(_columnSizes, ColumnGap, item.Column),
                bounds.Top + Offset(_rowSizes, RowGap, item.Row),
                Span(_columnSizes, ColumnGap, item.Column, item.ColumnSpan),
                Span(_rowSizes, RowGap, item.Row, item.RowSpan)));
        }
    }

    /// <inheritdoc />
    public override void Draw(TuiSurface surface)
    {
        foreach (var item in _items)
        {
            DrawChild(item.Content, surface);
        }
    }

    /// <summary>The declared tracks, padded out to however many the children use.</summary>
    private static TuiLength[] Track(IReadOnlyList<TuiLength> declared, int count, TuiLength fallback)
    {
        var tracks = new TuiLength[Math.Max(count, declared.Count)];

        for (var index = 0; index < tracks.Length; index += 1)
        {
            tracks[index] = index < declared.Count ? declared[index] : fallback;
        }

        return tracks;
    }

    private int[] Allocate(
        TuiLength[] tracks,
        int available,
        int gap,
        bool horizontal,
        TuiConstraints constraints)
    {
        // A track with nothing visible in it takes no cells, so a grid whose whole third
        // column is hidden closes up rather than leaving a stripe.
        var visible = new bool[tracks.Length];

        foreach (var item in _items.Where(item => item.Content.IsVisible))
        {
            var start = horizontal ? item.Column : item.Row;
            var span = horizontal ? item.ColumnSpan : item.RowSpan;

            for (var index = start; index < Math.Min(start + span, tracks.Length); index += 1)
            {
                visible[index] = true;
            }
        }

        var gaps = gap * Math.Max(0, visible.Count(shown => shown) - 1);

        return TuiTracks.Allocate(
            tracks,
            visible,
            Math.Max(0, available - gaps),
            index => Natural(index, horizontal, constraints));
    }

    /// <summary>
    /// How big an <c>auto</c> track wants to be: the largest of what is in it.
    /// </summary>
    /// <remarks>
    /// Children spanning more than one track are left out. Dividing one child's demand
    /// across several tracks is a whole algorithm of its own, and every answer to it is
    /// arbitrary — a spanning child is asking to fit whatever the others decided, which is
    /// what spanning usually means.
    /// </remarks>
    private int Natural(int index, bool horizontal, TuiConstraints constraints)
    {
        var largest = 0;

        foreach (var item in _items)
        {
            if (!item.Content.IsVisible)
            {
                continue;
            }

            var start = horizontal ? item.Column : item.Row;
            var span = horizontal ? item.ColumnSpan : item.RowSpan;

            if (start != index || span != 1)
            {
                continue;
            }

            var desired = item.Content.Measure(constraints);

            largest = Math.Max(largest, horizontal ? desired.Width : desired.Height);
        }

        return largest;
    }

    private static int Total(int[] sizes, int gap)
    {
        var used = sizes.Count(size => size > 0);

        return sizes.Sum() + (gap * Math.Max(0, used - 1));
    }

    /// <summary>Where a track starts, counting the tracks and gaps before it.</summary>
    private static int Offset(int[] sizes, int gap, int track)
    {
        var offset = 0;

        for (var index = 0; index < Math.Min(track, sizes.Length); index += 1)
        {
            offset += sizes[index];

            if (sizes[index] > 0)
            {
                offset += gap;
            }
        }

        return offset;
    }

    /// <summary>How wide a run of tracks is, gaps between them included.</summary>
    private static int Span(int[] sizes, int gap, int track, int span)
    {
        var total = 0;
        var counted = 0;

        for (var index = track; index < Math.Min(track + span, sizes.Length); index += 1)
        {
            total += sizes[index];

            if (sizes[index] > 0)
            {
                counted += 1;
            }
        }

        return total + (gap * Math.Max(0, counted - 1));
    }

    /// <summary>
    /// Reads <c>"auto, *, 2*, 25%"</c> into the tracks it names.
    /// </summary>
    /// <remarks>
    /// Commas, and not spaces. A length already uses a space to separate itself from its
    /// bounds — <c>auto 12..40</c> is one length, not two — so a space cannot also mean "next
    /// track". Splitting on both read <c>"auto 6.."</c> as two tracks and quietly gave the
    /// second one nothing, which is a layout that looks plausible and is wrong.
    /// </remarks>
    private static TuiLength[] Parse(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? []
            : [.. text
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(TuiLength.Parse)];
}
