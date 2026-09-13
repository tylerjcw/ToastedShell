using Tosh.Runtime;
using Tosh.Tui.Rendering;

namespace Tosh.Tui.Widgets;

/// <summary>
/// How far along something is, as a bar.
/// </summary>
/// <remarks>
/// <para>
/// Every script that reports progress writes this function, and each one gets the
/// rounding, the empty case and the width arithmetic slightly differently
/// (<c>TUI-0020</c>). <c>examples/system-monitor.tosh</c> had one.
/// </para>
/// <para>
/// The label is drawn <em>over</em> the bar rather than beside it, so a gauge is one row
/// however long its caption is, and the caption stays readable at both ends by taking the
/// style of whichever side it happens to sit on.
/// </para>
/// </remarks>
public sealed class TuiGauge : TuiWidget
{
    public TuiGauge(double amount = 0)
    {
        Amount = amount;
    }

    /// <summary>
    /// How far along, between <see cref="Minimum"/> and <see cref="Maximum"/>.
    /// </summary>
    /// <remarks>
    /// Not called <c>Value</c>, because <see cref="TuiWidget.Value"/> already means what a
    /// widget answers a form with, and a property that hides it would leave a gauge
    /// reporting nothing while looking like it reported something. <see cref="Value"/>
    /// answers with this.
    /// </remarks>
    public double Amount { get; set; }

    /// <summary>A script function supplying the amount, re-read on every redraw.</summary>
    public IShellCallable? AmountSource { get; set; }

    /// <inheritdoc />
    public override object? Value => Amount;

    /// <summary>What an empty bar represents.</summary>
    public double Minimum { get; set; }

    /// <summary>What a full bar represents.</summary>
    public double Maximum { get; set; } = 100;

    /// <summary>Written over the bar. Null shows the percentage; empty shows nothing.</summary>
    public string? Label { get; set; }

    /// <summary>The filled part.</summary>
    public string FilledGlyph { get; set; } = "█";

    /// <summary>The rest.</summary>
    public string EmptyGlyph { get; set; } = "·";

    /// <summary>How the filled part is drawn.</summary>
    public TuiStyle FilledStyle { get; set; }

    /// <summary>How the rest is drawn.</summary>
    public TuiStyle EmptyStyle { get; set; } = new(Attributes: TuiTextAttributes.Dim);

    /// <summary>Styles the filled part by how full it is, for a gauge that means something.</summary>
    public Func<double, TuiStyle>? StyleSelector { get; set; }

    /// <summary>How full, between nothing and one.</summary>
    public double Ratio => Maximum <= Minimum
        ? 0
        : Math.Clamp((Amount - Minimum) / (Maximum - Minimum), 0, 1);

    /// <inheritdoc />
    public override TuiSize Measure(TuiConstraints constraints)
        => constraints.Constrain(new TuiSize(constraints.MaxWidth == int.MaxValue ? 20 : constraints.MaxWidth, 1));

    /// <inheritdoc />
    public override void Draw(TuiSurface surface)
    {
        var width = surface.Width;

        if (width <= 0 || surface.Height <= 0)
        {
            return;
        }

        var filled = (int)Math.Round(Ratio * width, MidpointRounding.AwayFromZero);
        var style = StyleSelector?.Invoke(Ratio) ?? FilledStyle;

        for (var column = 0; column < width; column += 1)
        {
            var isFilled = column < filled;

            surface.DrawText(column, 0, isFilled ? FilledGlyph : EmptyGlyph, isFilled ? style : EmptyStyle);
        }

        var caption = Label ?? $"{Ratio * 100:0.#}%";

        if (caption.Length == 0)
        {
            return;
        }

        // Centred over the bar, each cell keeping the side it sits on, so the caption stays
        // legible whether the bar has reached it or not.
        var start = Math.Max(0, (width - TuiTextMeasure.MeasureWidth(caption)) / 2);
        var at = start;

        foreach (var cluster in TuiTextMeasure.EnumerateClusters(caption))
        {
            if (at >= width)
            {
                break;
            }

            surface.DrawText(at, 0, cluster, at < filled ? style : EmptyStyle);
            at += TuiTextMeasure.ClusterWidth(cluster);
        }
    }
}
