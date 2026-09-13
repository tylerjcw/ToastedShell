using Tosh.Tui;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

/// <summary>
/// A series in one row, and how far along something is.
/// </summary>
/// <remarks>
/// The sparkline follows the one in <c>pidwatch</c>, whose two good decisions are the
/// window onto the tail and the floor under the scale.
/// </remarks>
public sealed class TuiSparklineTests
{
    private static string Row(TuiWidget widget, int width)
    {
        var buffer = new TuiBuffer(new TuiSize(width, 1));
        var bounds = new TuiRect(0, 0, width, 1);

        widget.Measure(TuiConstraints.From(new TuiSize(width, 1)));
        widget.Arrange(bounds);
        widget.Draw(new TuiSurface(buffer, bounds));

        return buffer.RowText(0);
    }

    [Fact]
    public void A_series_is_drawn_from_eighths_of_a_block()
    {
        var spark = new TuiSparkline([0, 1, 2, 3, 4, 5, 6, 7]) { Maximum = 7 };

        Assert.Equal("▁▂▃▄▅▆▇█", Row(spark, 8));
    }

    [Fact]
    public void The_scale_has_a_floor_so_a_quiet_series_stays_quiet()
    {
        // pidwatch's best idea. Scaled to the observed maximum, 1-2-1-2 is a mountain
        // range; scaled against 100, it is the flat line it actually is.
        var amplified = new TuiSparkline([1, 2, 1, 2]);
        var honest = new TuiSparkline([1, 2, 1, 2]) { MinimumScale = 100 };

        // Both are measured from zero; what differs is where the top is. Against 2, one
        // is half the chart; against 100, it is the floor.
        Assert.Equal("▄█▄█", Row(amplified, 4));
        Assert.Equal("▁▁▁▁", Row(honest, 4));
    }

    [Fact]
    public void Only_the_tail_that_fits_is_drawn()
    {
        // A monitor pushes samples forever; the chart shows the last however-many fit.
        var spark = new TuiSparkline(Enumerable.Range(0, 10).Select(n => (double)n)) { Maximum = 9 };

        // Ten samples, four columns: the last four, rising.
        Assert.Equal("▅▆▇█", Row(spark, 4));
    }

    [Fact]
    public void A_short_series_fills_from_the_right_so_it_reads_as_time_passing()
    {
        var spark = new TuiSparkline([5, 5]) { Maximum = 5 };

        Assert.Equal("▁▁▁▁██", Row(spark, 6));
    }

    [Fact]
    public void Padding_can_be_turned_off_for_a_chart_that_is_not_a_clock()
    {
        var spark = new TuiSparkline([5, 5]) { Maximum = 5, PadLeft = false };

        Assert.Equal("██", Row(spark, 6).TrimEnd());
    }

    [Fact]
    public void Capacity_keeps_a_monitor_from_growing_a_list_it_never_trims()
    {
        var spark = new TuiSparkline { Capacity = 3 };

        foreach (var sample in (double[])[1, 2, 3, 4, 5])
        {
            spark.Push(sample);
        }

        Assert.Equal([3, 4, 5], spark.Values);
        Assert.Equal(5d, spark.Value);
    }

    [Fact]
    public void Each_column_can_be_styled_by_what_it_holds()
    {
        var spark = new TuiSparkline([10, 90]) { Maximum = 100 };
        spark.StyleSelector = (_, ratio) => new TuiStyle(ratio >= 0.75 ? "red" : "cyan");

        var buffer = new TuiBuffer(new TuiSize(2, 1));
        spark.Arrange(new TuiRect(0, 0, 2, 1));
        spark.Draw(new TuiSurface(buffer, new TuiRect(0, 0, 2, 1)));

        Assert.Equal("cyan", buffer[0, 0].Style.Foreground);
        Assert.Equal("red", buffer[1, 0].Style.Foreground);
    }

    [Fact]
    public void An_empty_series_draws_a_floor_rather_than_a_gap()
    {
        Assert.Equal("▁▁▁▁", Row(new TuiSparkline(), 4));
    }

    [Fact]
    public void A_gauge_fills_in_proportion_and_says_how_far()
    {
        var gauge = new TuiGauge(50);

        Assert.Equal(0.5, gauge.Ratio);
        Assert.Equal("████████50%·········", Row(gauge, 20));
    }

    [Fact]
    public void A_gauge_reports_its_amount_as_the_widget_value()
    {
        // `Value` is what a widget answers a form with, so a gauge in a form reports the
        // number rather than nothing.
        Assert.Equal(42d, new TuiGauge(42).Value);
    }

    [Fact]
    public void A_gauge_label_replaces_the_percentage_and_an_empty_one_removes_it()
    {
        Assert.Equal("████████busy········", Row(new TuiGauge(40) { Label = "busy" }, 20));
        Assert.Equal("████████············", Row(new TuiGauge(40) { Label = string.Empty }, 20));
    }

    [Fact]
    public void A_gauge_scales_between_its_own_bounds()
    {
        var gauge = new TuiGauge(15) { Minimum = 10, Maximum = 20, Label = string.Empty };

        Assert.Equal(0.5, gauge.Ratio);
        Assert.Equal("█████·····", Row(gauge, 10));
    }

    [Fact]
    public void A_gauge_is_empty_at_its_floor_and_full_at_its_ceiling()
    {
        Assert.Equal("··········", Row(new TuiGauge(0) { Label = string.Empty }, 10));
        Assert.Equal("██████████", Row(new TuiGauge(100) { Label = string.Empty }, 10));
        Assert.Equal("██████████", Row(new TuiGauge(9999) { Label = string.Empty }, 10));
    }
}
