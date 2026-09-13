using Tosh.Runtime;
using Tosh.Tui.Rendering;

namespace Tosh.Tui.Widgets;

/// <summary>
/// A series drawn in one row, out of eighths of a block.
/// </summary>
/// <remarks>
/// <para>
/// The shape of a number over time, in the space a number takes. A monitor wants one
/// beside every row it reports, and a script that wants one today writes a function that
/// repeats block characters — which is where the rounding, the empty case and the scale go
/// slightly differently every time (<c>TUI-0020</c>).
/// </para>
/// <para>
/// Modelled on the one in <c>pidwatch</c>, whose two good decisions are worth stating.
/// The chart is a window onto the <em>tail</em> of the series, so a monitor pushes samples
/// forever and the widget shows the last however-many that fit. And the scale has a floor:
/// a CPU percentage is drawn against 100 even when it never exceeds 4, because scaling to
/// the observed maximum turns a flat, quiet series into a mountain range.
/// </para>
/// <code>
/// var cpu = new TuiSparkline() {| MinimumScale = 100, Capacity = 120 |}
///
/// $cpu.Push($load)
/// </code>
/// </remarks>
public sealed class TuiSparkline : TuiWidget
{
    /// <summary>Eighths of a block, from nearly nothing to full height.</summary>
    private static readonly string[] Blocks = ["▁", "▂", "▃", "▄", "▅", "▆", "▇", "█"];

    private readonly List<double> _values = [];

    public TuiSparkline(IEnumerable<double>? values = null)
    {
        if (values is not null)
        {
            _values.AddRange(values);
        }
    }

    /// <summary>The series. Only the tail of it is drawn.</summary>
    public IReadOnlyList<double> Values
    {
        get => _values;
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            _values.Clear();
            _values.AddRange(value);
            Trim();
        }
    }

    /// <summary>A script function supplying the series, re-read on every redraw.</summary>
    public IShellCallable? ValuesSource { get; set; }

    /// <summary>
    /// How many samples to keep. Zero keeps everything.
    /// </summary>
    /// <remarks>
    /// A monitor pushing a sample a second for a day has 86,400 of them and draws thirty.
    /// Capping here rather than at the call site is what stops every such script growing a
    /// list it never trims.
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

    /// <summary>The value the bottom of the chart represents.</summary>
    public double Minimum { get; set; }

    /// <summary>The value the top represents, or null to take it from the series.</summary>
    public double? Maximum { get; set; }

    /// <summary>
    /// The lowest the top of the chart will go when it is taken from the series.
    /// </summary>
    /// <remarks>
    /// Zero by default, which means scale to whatever arrived. Set to 100 for a percentage
    /// and a quiet series stays quiet instead of being amplified into a mountain range.
    /// </remarks>
    public double MinimumScale { get; set; }

    /// <summary>How the chart is drawn.</summary>
    public TuiStyle Style { get; set; }

    /// <summary>
    /// Styles each column by what it holds.
    /// </summary>
    /// <remarks>
    /// Given the value and how far up the chart it reaches, so a monitor can colour the
    /// busy end without knowing what the scale worked out to.
    /// </remarks>
    public Func<double, double, TuiStyle>? StyleSelector { get; set; }

    /// <summary>
    /// Whether a series shorter than the chart is pushed to the right.
    /// </summary>
    /// <remarks>
    /// On, so a chart filling up from the left reads as time passing rather than as a
    /// series that keeps changing shape.
    /// </remarks>
    public bool PadLeft { get; set; } = true;

    /// <summary>Adds a sample, dropping the oldest once <see cref="Capacity"/> is reached.</summary>
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
    public override TuiSize Measure(TuiConstraints constraints)
        => constraints.Constrain(new TuiSize(_values.Count, 1));

    /// <inheritdoc />
    public override void Draw(TuiSurface surface)
    {
        var width = surface.Width;

        if (width <= 0 || surface.Height <= 0)
        {
            return;
        }

        var visible = _values.Count > width ? _values[^width..] : _values;
        var top = Scale(visible);
        var column = PadLeft ? width - visible.Count : 0;

        if (PadLeft)
        {
            // The empty part of a chart still reads as a chart: a dim floor, not a gap.
            for (var blank = 0; blank < column; blank += 1)
            {
                surface.DrawText(blank, 0, Blocks[0], new TuiStyle(Attributes: TuiTextAttributes.Dim));
            }
        }

        foreach (var value in visible)
        {
            var ratio = top <= Minimum ? 0 : Math.Clamp((value - Minimum) / (top - Minimum), 0, 1);
            var block = Blocks[(int)(ratio * (Blocks.Length - 1))];

            surface.DrawText(column, 0, block, StyleSelector?.Invoke(value, ratio) ?? Style);
            column += 1;
        }
    }

    /// <summary>What the top of the chart represents.</summary>
    private double Scale(IReadOnlyList<double> visible)
    {
        if (Maximum is { } fixedMaximum)
        {
            return fixedMaximum;
        }

        var observed = MinimumScale;

        foreach (var value in visible)
        {
            observed = Math.Max(observed, value);
        }

        return observed;
    }

    private void Trim()
    {
        if (Capacity > 0 && _values.Count > Capacity)
        {
            _values.RemoveRange(0, _values.Count - Capacity);
        }
    }
}
