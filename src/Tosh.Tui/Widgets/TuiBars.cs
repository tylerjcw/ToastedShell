using Tosh.Runtime;
using Tosh.Tui.Rendering;

namespace Tosh.Tui.Widgets;

/// <summary>One labelled value per row, drawn as a bar.</summary>
public sealed record TuiBar(string Label, double Amount);

/// <summary>
/// A bar per value, so several numbers can be compared at a glance.
/// </summary>
/// <remarks>
/// <para>
/// Where a <see cref="TuiGauge"/> says how far along one thing is, this says how several
/// compare — disk by mount, memory by process, time by stage. The labels are laid out in
/// one column so the bars start at the same place, which is the whole point of putting
/// them one above the other (<c>TUI-0020</c>).
/// </para>
/// <para>
/// The scale is shared: every bar is measured against the same maximum, or comparing them
/// says nothing. As with <see cref="TuiSparkline"/> that maximum has a floor, so a set of
/// small values is not amplified into a set of large ones.
/// </para>
/// </remarks>
public sealed class TuiBars : TuiWidget
{
    private IReadOnlyList<TuiBar> _bars = [];

    public TuiBars(IEnumerable<TuiBar>? bars = null)
    {
        if (bars is not null)
        {
            Bars = [.. bars];
        }
    }

    /// <summary>The values, one per row.</summary>
    public IReadOnlyList<TuiBar> Bars
    {
        get => _bars;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _bars = value;
        }
    }

    /// <summary>A script function supplying the values, re-read on every redraw.</summary>
    public IShellCallable? BarsSource { get; set; }

    /// <summary>The value a full bar represents, or null to take it from the values.</summary>
    public double? Maximum { get; set; }

    /// <summary>The lowest a maximum taken from the values will go.</summary>
    public double MinimumScale { get; set; }

    /// <summary>How wide the label column is, or null to measure it from the labels.</summary>
    public int? LabelWidth { get; set; }

    /// <summary>Whether each row ends with its own value.</summary>
    public bool ShowValues { get; set; } = true;

    /// <summary>How a value is written when it is shown.</summary>
    public Func<double, string> Format { get; set; } = value => value.ToString("0.#");

    /// <summary>The filled part of a bar.</summary>
    public string FilledGlyph { get; set; } = "█";

    /// <summary>The rest of it.</summary>
    public string EmptyGlyph { get; set; } = "·";

    /// <summary>How a label is drawn.</summary>
    public TuiStyle LabelStyle { get; set; }

    /// <summary>How the filled part is drawn.</summary>
    public TuiStyle FilledStyle { get; set; }

    /// <summary>How the rest is drawn.</summary>
    public TuiStyle EmptyStyle { get; set; } = new(Attributes: TuiTextAttributes.Dim);

    /// <summary>Styles a bar by how full it is.</summary>
    public Func<double, TuiStyle>? StyleSelector { get; set; }

    /// <inheritdoc />
    public override object? Value => _bars;

    /// <inheritdoc />
    protected override TuiSize MeasureCore(TuiConstraints constraints)
        => constraints.Constrain(new TuiSize(
            constraints.MaxWidth == int.MaxValue ? 40 : constraints.MaxWidth,
            _bars.Count));

    /// <inheritdoc />
    public override void Draw(TuiSurface surface)
    {
        if (_bars.Count == 0 || surface.Width <= 0)
        {
            return;
        }

        var labels = LabelWidth ?? _bars.Max(bar => TuiTextMeasure.MeasureWidth(bar.Label));
        var values = ShowValues ? _bars.Max(bar => TuiTextMeasure.MeasureWidth(Format(bar.Amount))) : 0;

        // A label column and a value column, each with one space of air, and the bar takes
        // whatever is left. Too narrow for a bar at all and the numbers are what survive.
        var bars = surface.Width - labels - 1 - (values > 0 ? values + 1 : 0);
        var top = Scale();

        for (var row = 0; row < surface.Height && row < _bars.Count; row += 1)
        {
            var bar = _bars[row];
            var ratio = top <= 0 ? 0 : Math.Clamp(bar.Amount / top, 0, 1);

            surface.DrawText(0, row, TuiTextMeasure.Truncate(bar.Label, labels), LabelStyle, labels);

            if (bars > 0)
            {
                var filled = (int)Math.Round(ratio * bars, MidpointRounding.AwayFromZero);
                var style = StyleSelector?.Invoke(ratio) ?? FilledStyle;

                for (var column = 0; column < bars; column += 1)
                {
                    var isFilled = column < filled;

                    surface.DrawText(
                        labels + 1 + column,
                        row,
                        isFilled ? FilledGlyph : EmptyGlyph,
                        isFilled ? style : EmptyStyle);
                }
            }

            if (values > 0)
            {
                var text = Format(bar.Amount);

                surface.DrawText(
                    surface.Width - TuiTextMeasure.MeasureWidth(text),
                    row,
                    text,
                    LabelStyle);
            }
        }
    }

    /// <summary>What a full bar represents, shared by every row.</summary>
    private double Scale()
    {
        if (Maximum is { } fixedMaximum)
        {
            return fixedMaximum;
        }

        var observed = MinimumScale;

        foreach (var bar in _bars)
        {
            observed = Math.Max(observed, bar.Amount);
        }

        return observed;
    }
}
