using Tosh.Tui;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

/// <summary>
/// A series drawn against a scale the reader can read (<c>TUI-0028</c>).
/// </summary>
/// <remarks>
/// The sparkline and the bars deliberately have no axis. This is the one that does, and the
/// axis is the whole reason to reach for it: without numbers a chart is a decoration.
/// </remarks>
public sealed class TuiChartTests
{
    private static IReadOnlyList<string> Render(TuiChart chart, int width, int height)
    {
        var buffer = new TuiBuffer(new TuiSize(width, height));

        chart.Measure(new TuiConstraints(width, height));
        chart.Arrange(new TuiRect(0, 0, width, height));
        chart.Draw(new TuiSurface(buffer, new TuiRect(0, 0, width, height)));

        return [.. Enumerable.Range(0, height).Select(row => buffer.RowText(row).TrimEnd())];
    }

    // ── The scale ────────────────────────────────────────────────────

    /// <summary>
    /// Ticks are numbers a person would have chosen.
    /// </summary>
    /// <remarks>
    /// Dividing a range into equal parts gives 0, 33.3, 66.6 — arithmetically fine and
    /// unreadable. A step is a small round multiple of a power of ten, and the candidates
    /// include 2.5 so that a percentage axis reads 0, 25, 50, 75, 100 rather than stepping
    /// by 20.
    /// </remarks>
    [Theory]
    [InlineData(0, 100, 5, 0, 100, 25)]
    [InlineData(0, 97, 5, 0, 100, 25)]
    [InlineData(0, 8, 5, 0, 8, 2)]
    [InlineData(0, 1, 5, 0, 1, 0.25)]
    [InlineData(3, 17, 5, 0, 20, 5)]
    public void Ticks_are_numbers_somebody_would_have_chosen(
        double low,
        double high,
        int wanted,
        double expectedMinimum,
        double expectedMaximum,
        double expectedStep)
    {
        var scale = TuiTicks.Choose(low, high, wanted);

        Assert.Equal(expectedMinimum, scale.Minimum, 6);
        Assert.Equal(expectedMaximum, scale.Maximum, 6);
        Assert.Equal(expectedStep, scale.Step, 6);
    }

    /// <summary>The axis always reaches the data.</summary>
    /// <remarks>
    /// The property that matters more than any particular step: an axis that stopped short
    /// of a value would draw that value outside the plot.
    /// </remarks>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 7)]
    [InlineData(-14, 3)]
    [InlineData(0.001, 0.007)]
    [InlineData(1000, 999999)]
    public void The_scale_always_covers_the_data(double low, double high)
    {
        var scale = TuiTicks.Choose(low, high, 5);

        Assert.True(scale.Minimum <= low, $"{scale.Minimum} > {low}");
        Assert.True(scale.Maximum >= high, $"{scale.Maximum} < {high}");
    }

    /// <summary>A flat series still gets an axis rather than a division by zero.</summary>
    [Fact]
    public void A_series_that_never_changes_still_has_a_scale()
    {
        var scale = TuiTicks.Choose(42, 42, 5);

        Assert.True(scale.Minimum < 42);
        Assert.True(scale.Maximum > 42);
        Assert.True(scale.Step > 0);
    }

    /// <summary>
    /// Tick values are counted, not accumulated.
    /// </summary>
    /// <remarks>
    /// Adding 0.1 ten times is 0.9999999999999999, and an axis labelled
    /// 0.7000000000000001 is an axis nobody trusts.
    /// </remarks>
    [Fact]
    public void Tick_values_do_not_drift()
    {
        var labels = TuiTicks.Choose(0, 1, 5).Values.Select(TuiTicks.Choose(0, 1, 5).Format);

        Assert.Equal(["0.00", "0.25", "0.50", "0.75", "1.00"], labels);
    }

    /// <summary>The step decides the precision, so the labels line up.</summary>
    /// <remarks>
    /// Formatting each label from its own value gives a column of different widths, which
    /// is how an axis comes out ragged.
    /// </remarks>
    [Fact]
    public void Labels_are_formatted_from_the_step_rather_than_the_value()
    {
        var scale = TuiTicks.Choose(0, 100, 5);

        Assert.All(scale.Values.Select(scale.Format), label => Assert.DoesNotContain('.', label));
    }

    // ── The chart ────────────────────────────────────────────────────

    [Fact]
    public void The_axis_is_labelled_and_has_a_spine()
    {
        var rows = Render(new TuiChart([0, 50, 100]) { MinimumScale = 100 }, 30, 7);

        Assert.Contains(rows, row => row.StartsWith("100┤", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.StartsWith("  0┤", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.StartsWith("   │", StringComparison.Ordinal));
    }

    [Fact]
    public void The_bottom_row_is_a_horizontal_axis()
    {
        var rows = Render(new TuiChart([1, 2, 3, 4, 5, 6, 7, 8]), 30, 8);

        Assert.StartsWith("└", rows[^1].TrimStart()[..1], StringComparison.Ordinal);
        Assert.Contains("─", rows[^1], StringComparison.Ordinal);
    }

    /// <summary>
    /// A chart too narrow for labels loses them rather than losing the plot.
    /// </summary>
    /// <remarks>
    /// A chart with no numbers is worth less than one with numbers, and worth far more than
    /// a column of labels beside two cells of data.
    /// </remarks>
    [Fact]
    public void The_axis_is_dropped_before_the_plot_is()
    {
        var rows = Render(new TuiChart([10, 20, 30]) { MinimumScale = 100 }, 6, 5);

        Assert.DoesNotContain(rows, row => row.Contains('┤', StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Contains('█', StringComparison.Ordinal));
    }

    /// <summary>No row is reserved for an axis that is not going to be drawn.</summary>
    /// <remarks>
    /// The first version held a row back for the horizontal axis and then decided there was
    /// no room to draw one, which left a blank line at the foot of every narrow chart.
    /// </remarks>
    [Fact]
    public void A_chart_too_small_for_a_horizontal_axis_does_not_reserve_its_row()
    {
        var rows = Render(new TuiChart([10, 20, 30, 40]) { MinimumScale = 100 }, 10, 4);

        Assert.NotEqual(string.Empty, rows[^1]);
    }

    /// <summary>
    /// A short series fills the plot rather than being pinned to one side.
    /// </summary>
    /// <remarks>
    /// The default differs from the sparkline's on purpose: a sparkline is a live shape and
    /// a chart with an axis is a report, and a report that leaves three-quarters of its plot
    /// empty has misrepresented how much data there is.
    /// </remarks>
    [Fact]
    public void A_short_series_is_spread_across_the_plot()
    {
        var rows = Render(new TuiChart([10, 20, 30, 40]) { MinimumScale = 40 }, 34, 8);
        var plot = rows[^2][4..];

        Assert.DoesNotContain(' ', plot.TrimEnd());
    }

    /// <summary>Windowed instead, when a live meter asks for it.</summary>
    [Fact]
    public void A_tail_window_is_still_available()
    {
        var chart = new TuiChart(Enumerable.Range(0, 200).Select(value => (double)value))
        {
            Stretch = false,
        };

        var rows = Render(chart, 40, 8);

        // The last forty samples climb steadily, so the newest column is the tallest.
        Assert.Contains('█', rows[^2]);
    }

    /// <summary>
    /// Downsampling keeps the peak in each bucket.
    /// </summary>
    /// <remarks>
    /// A mean would be defensible and is wrong here: a chart that averages away the spike
    /// is a chart that hides the incident the reader opened it to find.
    /// </remarks>
    [Fact]
    public void A_spike_survives_being_squeezed_into_fewer_columns()
    {
        var values = new double[400];

        values[137] = 100;

        var rows = Render(new TuiChart(values) { MinimumScale = 100 }, 24, 8);

        Assert.Contains(rows, row => row.Contains('█', StringComparison.Ordinal));
    }

    /// <summary>The floor under the scale, which is the sparkline's rule.</summary>
    /// <remarks>
    /// Without it a quiet series of 1-2-1-2 scales to its own maximum and draws as a
    /// mountain range, which is exactly the reading a monitor must not give.
    /// </remarks>
    [Fact]
    public void A_quiet_series_is_drawn_against_its_floor_not_its_own_maximum()
    {
        var rows = Render(new TuiChart([1, 2, 1, 2]) { MinimumScale = 100 }, 30, 8);

        // Everything sits on the baseline rather than filling the plot.
        Assert.DoesNotContain(rows[1], character => character == '█');
        Assert.Contains(rows, row => row.StartsWith("100┤", StringComparison.Ordinal));
    }

    [Fact]
    public void Braille_plots_two_points_per_cell()
    {
        var chart = new TuiChart(Enumerable.Range(0, 40).Select(value => (double)value))
        {
            Marks = TuiChartMarks.Braille,
        };

        var rows = Render(chart, 30, 8);

        Assert.Contains(rows, row => row.Any(character => character is >= '⠀' and <= '⣿'));
    }

    [Fact]
    public void Ascii_marks_need_nothing_above_the_ASCII_range()
    {
        var chart = new TuiChart([10, 20, 30, 40])
        {
            Marks = TuiChartMarks.Ascii,
            ShowAxis = false,
            ShowHorizontalAxis = false,
        };

        var rows = Render(chart, 20, 6);

        Assert.All(rows, row => Assert.All(row, character => Assert.InRange(character, ' ', '~')));
        Assert.Contains(rows, row => row.Contains('#', StringComparison.Ordinal));
    }

    [Fact]
    public void An_empty_chart_draws_its_axis_and_nothing_else()
    {
        var rows = Render(new TuiChart { MinimumScale = 100 }, 30, 7);

        Assert.Contains(rows, row => row.Contains('┤', StringComparison.Ordinal));
        Assert.DoesNotContain(rows, row => row.Contains('█', StringComparison.Ordinal));
    }

    [Fact]
    public void Capacity_forgets_the_oldest_samples()
    {
        var chart = new TuiChart { Capacity = 3 };

        foreach (var value in new double[] { 1, 2, 3, 4, 5 })
        {
            chart.Push(value);
        }

        Assert.Equal([3, 4, 5], chart.Values);
    }

    [Fact]
    public void The_value_a_form_reads_is_the_newest_sample()
    {
        Assert.Equal(5d, new TuiChart([1, 5]).Value);
        Assert.Null(new TuiChart().Value);
    }

    // ── The braille canvas ───────────────────────────────────────────

    /// <summary>
    /// The bit layout is the one the Unicode block was given, not the obvious one.
    /// </summary>
    /// <remarks>
    /// The fourth row of dots was added after the first three, so its two bits sit above
    /// the others rather than in sequence. Getting this wrong draws a chart that looks
    /// plausible and is upside down in its bottom quarter.
    /// </remarks>
    [Theory]
    [InlineData(0, 0, '⠁')]
    [InlineData(0, 1, '⠂')]
    [InlineData(0, 2, '⠄')]
    [InlineData(0, 3, '⡀')]
    [InlineData(1, 0, '⠈')]
    [InlineData(1, 1, '⠐')]
    [InlineData(1, 2, '⠠')]
    [InlineData(1, 3, '⢀')]
    public void One_dot_is_the_character_Unicode_says_it_is(int column, int row, char expected)
    {
        var canvas = new TuiBrailleCanvas(1, 1);

        canvas.Set(column, row);

        Assert.Equal(expected.ToString(), canvas.Cell(0, 0));
    }

    [Fact]
    public void An_empty_cell_is_a_space_rather_than_a_blank_braille_pattern()
    {
        // They look the same and are not: the blank is a character the ASCII folding would
        // turn into a mark, so an empty chart would come out stippled.
        Assert.Equal(" ", new TuiBrailleCanvas(1, 1).Cell(0, 0));
    }

    /// <summary>
    /// A point outside the canvas is dropped, not clamped.
    /// </summary>
    /// <remarks>
    /// Clamping would pile everything that overflowed onto the edge row, drawing a solid
    /// line along the top of a chart whose scale is too small — and hiding the fact that
    /// the scale is too small.
    /// </remarks>
    [Fact]
    public void A_point_off_the_canvas_is_dropped()
    {
        var canvas = new TuiBrailleCanvas(1, 1);

        canvas.Set(-1, 0);
        canvas.Set(0, -1);
        canvas.Set(99, 0);
        canvas.Set(0, 99);

        Assert.Equal(" ", canvas.Cell(0, 0));
    }

    // ── The shared ramp ──────────────────────────────────────────────

    /// <summary>
    /// A value that is present at all draws something.
    /// </summary>
    /// <remarks>
    /// Rounded away from zero, because a chart whose smallest bar is indistinguishable from
    /// no bar has lost the reader's smallest data point.
    /// </remarks>
    [Fact]
    public void The_smallest_fraction_still_draws()
    {
        Assert.Equal("▁", TuiBlocks.Eighth(0.001));
        Assert.Null(TuiBlocks.Eighth(0));
        Assert.Equal("█", TuiBlocks.Eighth(1));
        Assert.Equal("█", TuiBlocks.Eighth(5));
    }
}
