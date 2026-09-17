using Tosh.Runtime;
using Tosh.Tui.Rendering;

namespace Tosh.Tui.Widgets;

/// <summary>What a chart's points are drawn out of.</summary>
public enum TuiChartMarks
{
    /// <summary>
    /// Block elements: eighths of a cell, filled from the baseline up.
    /// </summary>
    /// <remarks>
    /// Reads as an area chart, works on any terminal with the block characters, and is
    /// the right default — a chart that draws as boxes is worth less than a coarse one.
    /// </remarks>
    Blocks,

    /// <summary>
    /// Braille dots: eight times the resolution, and a line rather than a staircase.
    /// </summary>
    /// <remarks>
    /// Wants a font with the braille block in it, which is why it is asked for rather than
    /// assumed. On a terminal without one the writer folds it (<c>TUI-0009</c>), but a
    /// folded line is a row of marks — pick <see cref="Blocks"/> if the terminal is known
    /// to be poor.
    /// </remarks>
    Braille,

    /// <summary>One character per point, for a terminal that has nothing else.</summary>
    Ascii,
}

/// <summary>
/// A series drawn against a scale the reader can read.
/// </summary>
/// <remarks>
/// <para>
/// The other three charts deliberately have no axis. A <see cref="TuiSparkline"/> is a
/// shape, and a <see cref="TuiBars"/> chart's scale is the labels beside it. That covers
/// what a shell script usually reports and stops short of what it sometimes needs: how long
/// each build took over the last fifty commits, memory against time, a latency
/// distribution. Without an axis the reader has a shape and no numbers, and a shape with no
/// numbers is a decoration (<c>TUI-0028</c>).
/// </para>
/// <para>
/// The scale rules are the sparkline's, for the same reasons. The chart is a window onto
/// the <em>tail</em> of the series, so a monitor pushes samples forever and the widget draws
/// the last however-many fit; and <see cref="MinimumScale"/> is a floor, so a CPU percentage
/// is drawn against 100 even when it never exceeds 4. Without that floor a quiet series of
/// 1-2-1-2 scales to its own maximum and draws as a mountain range.
/// </para>
/// </remarks>
public sealed class TuiChart : TuiWidget
{
    private readonly List<double> _values = [];

    public TuiChart(IEnumerable<double>? values = null)
    {
        if (values is not null)
        {
            _values.AddRange(values);
        }
    }

    /// <summary>The series, oldest first.</summary>
    public IReadOnlyList<double> Values
    {
        get => _values;
        set
        {
            _values.Clear();

            if (value is not null)
            {
                _values.AddRange(value);
            }

            Trim();
        }
    }

    /// <summary>A function re-asked each redraw for the series.</summary>
    public IShellCallable? ValuesSource { get; set; }

    /// <summary>How many samples to keep, or zero for all of them.</summary>
    /// <remarks>
    /// Capped in the widget rather than at the call site, because a script sampling once a
    /// second for a day otherwise grows a list of 86,400 numbers to draw forty of them.
    /// </remarks>
    public int Capacity
    {
        get;
        set
        {
            field = Math.Max(0, value);
            Trim();
        }
    }

    /// <summary>What a point is drawn out of.</summary>
    public TuiChartMarks Marks { get; set; } = TuiChartMarks.Blocks;

    /// <summary>The bottom of the scale, when it should not be the smallest value.</summary>
    public double? Minimum { get; set; }

    /// <summary>The top of the scale, when it should not be the largest value.</summary>
    public double? Maximum { get; set; }

    /// <summary>A floor under the top of the scale.</summary>
    public double MinimumScale { get; set; }

    /// <summary>Whether to draw the axis at all.</summary>
    /// <remarks>
    /// Off, this is a sparkline with more rows. The axis is the reason to reach for this
    /// widget, so it is on — but a chart in a pane too narrow for labels should lose them
    /// rather than lose the plot, which <see cref="Draw"/> decides for itself.
    /// </remarks>
    public bool ShowAxis { get; set; } = true;

    /// <summary>Labels along the bottom, when the series is long enough to need them.</summary>
    public bool ShowHorizontalAxis { get; set; } = true;

    /// <summary>Whether the series is spread across the plot or windowed at its tail.</summary>
    /// <remarks>
    /// <para>
    /// On, the whole series fills the width: eight build times in a forty-column pane are
    /// five columns each. Off, the plot is a window onto the newest samples, one per
    /// column, which is what <see cref="TuiSparkline"/> does and what a live meter wants.
    /// </para>
    /// <para>
    /// The default differs from the sparkline's on purpose. A sparkline is a live shape and
    /// a chart with an axis is a report — and a report that leaves three-quarters of its
    /// plot empty has misrepresented how much data there is.
    /// </para>
    /// </remarks>
    public bool Stretch { get; set; } = true;

    /// <summary>How the horizontal axis labels a sample, given its index in the series.</summary>
    /// <remarks>
    /// The index by default, which is what a series with no other x has. A script plotting
    /// against time sets this to turn an index into a clock.
    /// </remarks>
    public Func<int, string>? LabelAt { get; set; }

    /// <summary>The style of the plotted series.</summary>
    public TuiStyle Style { get; set; }

    /// <summary>The style of the axis, its ticks and its labels.</summary>
    public TuiStyle AxisStyle { get; set; } = new(Attributes: TuiTextAttributes.Dim);

    /// <summary>A style per point, given its value and where it sits on the scale.</summary>
    public Func<double, double, TuiStyle>? StyleSelector { get; set; }

    /// <summary>Adds one sample.</summary>
    public void Push(double value)
    {
        _values.Add(value);
        Trim();
    }

    /// <summary>Forgets every sample.</summary>
    public void Clear() => _values.Clear();

    /// <inheritdoc />
    public override object? Value => _values.Count == 0 ? null : _values[^1];

    /// <inheritdoc />
    /// <remarks>
    /// As wide as the series plus its axis, and as tall as a chart needs to be worth
    /// drawing. Five rows is that: fewer than four and the ticks have nowhere to go.
    /// </remarks>
    protected override TuiSize MeasureCore(TuiConstraints constraints)
        => constraints.Constrain(new TuiSize(_values.Count + 6, 6));

    /// <inheritdoc />
    public override void Draw(TuiSurface surface)
    {
        if (surface.Width <= 0 || surface.Height <= 0)
        {
            return;
        }

        var scale = Scale(surface.Height);
        var labels = ShowAxis ? AxisLabels(scale) : [];

        // The axis is dropped rather than the plot when there is no room for both: a chart
        // with no numbers is worth less than one with numbers, and worth far more than a
        // column of labels beside two cells of data.
        var axisWidth = labels.Count > 0 ? labels.Max(label => label.Length) + 1 : 0;

        if (axisWidth + 4 > surface.Width)
        {
            axisWidth = 0;
        }

        var bottom = surface.Height - 1;

        // Reserved only when it will be drawn. A row held back for an axis that then
        // decides there is no room for it is a blank line at the foot of the chart, and
        // the chart was a row taller than it needed to be.
        var footer = ShowHorizontalAxis &&
                     surface.Height >= 4 &&
                     surface.Width - axisWidth >= 8 &&
                     _values.Count > 0
            ? 1
            : 0;

        var plotHeight = surface.Height - footer;
        var plotWidth = surface.Width - axisWidth;

        if (plotWidth <= 0 || plotHeight <= 0)
        {
            return;
        }

        if (axisWidth > 0)
        {
            DrawVerticalAxis(surface, scale, labels, axisWidth, plotHeight);
        }

        var (points, oldest) = Visible(plotWidth);

        DrawPlot(surface, scale, points, axisWidth, plotWidth, plotHeight);

        if (footer == 1)
        {
            DrawHorizontalAxis(surface, oldest, axisWidth, plotWidth, bottom);
        }
    }

    /// <summary>
    /// The series as the plot will show it: one value per plottable column.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Braille plots two points per cell, so a braille chart has twice as many columns to
    /// fill — which is the other half of what the resolution buys.
    /// </para>
    /// <para>
    /// Stretched, every column is a bucket of the whole series and takes the
    /// <em>largest</em> value in it. A mean would be defensible and is wrong here: a chart
    /// that averages away the spike is a chart that hides the incident the reader opened it
    /// to find.
    /// </para>
    /// </remarks>
    private (IReadOnlyList<double> Points, int Oldest) Visible(int plotWidth)
    {
        var columns = Marks == TuiChartMarks.Braille ? plotWidth * 2 : plotWidth;

        if (columns <= 0 || _values.Count == 0)
        {
            return ([], 0);
        }

        if (!Stretch)
        {
            return _values.Count > columns
                ? (_values[^columns..], _values.Count - columns)
                : (_values, 0);
        }

        if (_values.Count <= columns)
        {
            // Fewer samples than columns: each one is widened rather than the chart being
            // left mostly empty.
            var spread = new double[columns];

            for (var column = 0; column < columns; column += 1)
            {
                spread[column] = _values[(int)((long)column * _values.Count / columns)];
            }

            return (spread, 0);
        }

        var buckets = new double[columns];

        for (var column = 0; column < columns; column += 1)
        {
            var from = (int)((long)column * _values.Count / columns);
            var to = (int)((long)(column + 1) * _values.Count / columns);
            var peak = _values[from];

            for (var index = from + 1; index < Math.Max(to, from + 1); index += 1)
            {
                peak = Math.Max(peak, _values[index]);
            }

            buckets[column] = peak;
        }

        return (buckets, 0);
    }

    private IReadOnlyList<string> AxisLabels(TuiScale scale)
        => [.. scale.Values.Select(scale.Format)];

    private void DrawVerticalAxis(
        TuiSurface surface,
        TuiScale scale,
        IReadOnlyList<string> labels,
        int axisWidth,
        int plotHeight)
    {
        var values = scale.Values.ToArray();
        var ticked = new HashSet<int>();

        for (var index = 0; index < values.Length; index += 1)
        {
            // The scale runs bottom-up and the screen runs top-down.
            var row = plotHeight - 1 - (int)Math.Round(scale.Fraction(values[index]) * (plotHeight - 1));

            if (row < 0 || row >= plotHeight || !ticked.Add(row))
            {
                // Two ticks landing on one row happens when the plot is shorter than the
                // scale has marks. The lower one is already drawn and is the one whose
                // label is right for the row.
                continue;
            }

            var label = labels[index];

            surface.DrawText(axisWidth - 1 - label.Length, row, label, AxisStyle);
            surface.DrawText(axisWidth - 1, row, "┤", AxisStyle);
        }

        // The spine, wherever a tick did not claim the row.
        for (var row = 0; row < plotHeight; row += 1)
        {
            if (!ticked.Contains(row))
            {
                surface.DrawText(axisWidth - 1, row, "│", AxisStyle);
            }
        }
    }

    private void DrawPlot(
        TuiSurface surface,
        TuiScale scale,
        IReadOnlyList<double> visible,
        int left,
        int plotWidth,
        int plotHeight)
    {
        if (visible.Count == 0)
        {
            return;
        }

        if (Marks == TuiChartMarks.Braille)
        {
            DrawBraille(surface, scale, visible, left, plotWidth, plotHeight);
            return;
        }

        // Right-aligned when the series does not fill the plot, so a chart filling up reads
        // as time passing rather than as a shape that keeps changing. Stretched, the series
        // is already exactly as wide as the plot and this is a no-op.
        var column = left + plotWidth - visible.Count;

        foreach (var value in visible)
        {
            var fraction = scale.Fraction(value);
            var filled = fraction * plotHeight;

            for (var row = 0; row < plotHeight; row += 1)
            {
                var fromBottom = plotHeight - 1 - row;
                var cell = Math.Clamp(filled - fromBottom, 0, 1);

                var glyph = Marks == TuiChartMarks.Ascii
                    ? (cell > 0 ? "#" : null)
                    : TuiBlocks.Eighth(cell);

                if (glyph is not null)
                {
                    surface.DrawText(column, row, glyph, StyleSelector?.Invoke(value, fraction) ?? Style);
                }
            }

            column += 1;
        }
    }

    private void DrawBraille(
        TuiSurface surface,
        TuiScale scale,
        IReadOnlyList<double> visible,
        int left,
        int plotWidth,
        int plotHeight)
    {
        var canvas = new TuiBrailleCanvas(plotWidth, plotHeight);
        var column = Math.Max(0, canvas.Columns - visible.Count);
        var previous = -1;

        foreach (var value in visible)
        {
            var row = canvas.Rows - 1 - (int)Math.Round(scale.Fraction(value) * (canvas.Rows - 1));

            // Joined to the sample before it, so a series that climbs quickly is a line
            // rather than a row of disconnected dots.
            canvas.SetColumn(column, previous < 0 ? row : previous, row);

            previous = row;
            column += 1;
        }

        for (var row = 0; row < plotHeight; row += 1)
        {
            for (var cell = 0; cell < plotWidth; cell += 1)
            {
                var text = canvas.Cell(cell, row);

                if (text != " ")
                {
                    surface.DrawText(left + cell, row, text, Style);
                }
            }
        }
    }

    /// <summary>
    /// Labels under the plot, at both ends and in the middle where there is room.
    /// </summary>
    /// <remarks>
    /// Three labels rather than a tick per column: the horizontal axis of a time series is
    /// read for its extent — where it starts and where it ends — and a row of numbers under
    /// every column is unreadable at terminal widths.
    /// </remarks>
    private void DrawHorizontalAxis(
        TuiSurface surface,
        int oldest,
        int left,
        int plotWidth,
        int row)
    {
        if (_values.Count == 0 || plotWidth < 8)
        {
            return;
        }

        surface.DrawText(left, row, new string('─', plotWidth), AxisStyle);

        if (left > 0)
        {
            surface.DrawText(left - 1, row, "└", AxisStyle);
        }

        var first = Label(oldest);
        var last = Label(_values.Count - 1);

        surface.DrawText(left, row, first, AxisStyle);

        if (plotWidth - last.Length >= first.Length + 1)
        {
            surface.DrawText(left + plotWidth - last.Length, row, last, AxisStyle);
        }

        var middle = Label(oldest + ((_values.Count - 1 - oldest) / 2));
        var at = left + ((plotWidth - middle.Length) / 2);

        if (at > left + first.Length && at + middle.Length < left + plotWidth - last.Length)
        {
            surface.DrawText(at, row, middle, AxisStyle);
        }

        string Label(int index) => LabelAt?.Invoke(index) ?? index.ToString();
    }

    /// <summary>The scale the plot is drawn against.</summary>
    private TuiScale Scale(int height)
    {
        var lowest = Minimum ?? 0d;
        var highest = MinimumScale;

        foreach (var value in _values)
        {
            if (Minimum is null)
            {
                lowest = Math.Min(lowest, value);
            }

            highest = Math.Max(highest, value);
        }

        if (Maximum is { } fixedMaximum)
        {
            highest = fixedMaximum;
        }

        // One tick per two rows, so the labels have room to breathe; at least two, because
        // an axis with one mark on it says nothing about the space between marks.
        return TuiTicks.Choose(lowest, highest, Math.Max(2, height / 2));
    }

    private void Trim()
    {
        if (Capacity > 0 && _values.Count > Capacity)
        {
            _values.RemoveRange(0, _values.Count - Capacity);
        }
    }
}
