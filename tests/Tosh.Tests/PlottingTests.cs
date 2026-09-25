using System.Drawing;
using Tosh.Language;
using Tosh.Runtime;
using Tosh.Stdlib.Plotting;
using Xunit;

namespace Tosh.Tests;

public sealed class PlottingTests
{
    [Fact]
    public void LinearScale_maps_data_and_pixels_correctly()
    {
        var scale = new LinearScale(0.0, 100.0, 0f, 500f);

        Assert.Equal(0f, scale.ToPixel(0.0));
        Assert.Equal(250f, scale.ToPixel(50.0));
        Assert.Equal(500f, scale.ToPixel(100.0));

        Assert.Equal(0.0, scale.ToData(0f), 4);
        Assert.Equal(50.0, scale.ToData(250f), 4);
        Assert.Equal(100.0, scale.ToData(500f), 4);
    }

    [Fact]
    public void TickGenerator_generates_clean_ticks()
    {
        var ticks = TickGenerator.GenerateLinearTicks(0.0, 10.0, 5);
        Assert.Contains(0.0, ticks);
        Assert.Contains(10.0, ticks);
        Assert.True(ticks.Count >= 3);
    }

    [Fact]
    public void Figure_Plot_generates_valid_svg()
    {
        var fig = new Figure(title: "Unit Test Chart");
        fig.Plot([1.0, 4.0, 9.0, 16.0, 25.0], label: "Quadratic");

        var svg = fig.ToSvg();
        Assert.StartsWith("<svg", svg);
        Assert.EndsWith("</svg>", svg);
        Assert.Contains("Unit Test Chart", svg);
        Assert.Contains("Quadratic", svg);
        Assert.Contains("<path", svg);
    }

    [Fact]
    public void Figure_Plot_generates_terminal_braille()
    {
        var fig = new Figure(title: "Terminal Chart");
        fig.Plot([0.0, 10.0, 20.0, 30.0]);

        var term = fig.ToTerminalString(80, 20);
        Assert.Contains("Terminal Chart", term);
        Assert.Contains("┤", term);
        Assert.Contains("└", term);
    }

    [Fact]
    public void Scatter_and_Bar_series_work_in_figure()
    {
        var fig = new Figure();
        fig.Scatter([1.0, 2.0, 3.0], [10.0, 20.0, 30.0], label: "Dots");
        fig.Bar(["Apples", "Bananas", "Cherries"], [12.0, 25.0, 8.0], label: "Fruits");

        var svg = fig.ToSvg();
        Assert.Contains("Dots", svg);
        Assert.Contains("Fruits", svg);
        Assert.Contains("<circle", svg);
        Assert.Contains("<rect", svg);
    }

    [Fact]
    public async Task Plot_command_executes_in_engine()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);

        var results = await engine.ExecuteToListAsync("plot [2, 4, 6, 8] --title 'Engine Plot'");
        Assert.Single(results);
        Assert.IsType<Figure>(results[0]);

        var fig = (Figure)results[0]!;
        Assert.Equal("Engine Plot", fig.Title);
        Assert.Single(fig.PrimaryAxes.Series);
    }

    [Fact]
    public async Task Bar_command_executes_in_engine()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);

        var results = await engine.ExecuteToListAsync("bar ['Jan', 'Feb', 'Mar'] [100, 150, 120]");
        Assert.Single(results);
        Assert.IsType<Figure>(results[0]);

        var fig = (Figure)results[0]!;
        Assert.Single(fig.PrimaryAxes.Series);
        Assert.IsType<BarSeries>(fig.PrimaryAxes.Series[0]);
    }
}
