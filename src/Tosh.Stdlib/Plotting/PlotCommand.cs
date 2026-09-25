using System.Drawing;
using System.Globalization;
using Tosh.Runtime;

namespace Tosh.Stdlib.Plotting;

[CommandCategory("Plotting")]
[CommandArgument("y-or-x", "Data values for Y axis, or X values when Y is also supplied.", Required = false)]
[CommandArgument("y", "Data values for Y axis when X values are given as first argument.", Required = false)]
[CommandExample("plot [1, 4, 9, 16, 25]", Title = "Plot an array of numbers")]
[CommandExample("1..10 | map ($val ** 2) | plot", Title = "Plot pipeline numbers")]
[CommandExample("plot [0, 1, 2, 3] [0, 1, 4, 9]", Title = "Plot X and Y coordinates")]
[CommandOutput("A Figure instance that renders as a visual chart in the terminal or exports to SVG.", ClrType = typeof(IAsyncEnumerable<Figure>))]
[PipelineInput(AcceptsScalar = true, Description = "Accepts stream or list of numbers, pairs, or records to plot.")]
public sealed class PlotCommand : ShellCommand
{
    public PlotCommand()
        : base("plot", "Generates a line plot from coordinates, lists, or piped data.", "plot [x] [y] [--title <text>] [--label <text>] [--color <color>] [--save <file.svg>]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        string? title = null;
        string? label = null;
        Color? color = null;
        string? savePath = null;
        var positionalArgs = new List<object?>();

        for (int i = 0; i < context.Arguments.Count; i++)
        {
            var argStr = context.Arguments[i]?.ToString() ?? "";
            if (argStr == "--title" && i + 1 < context.Arguments.Count)
            {
                title = context.Arguments[++i]?.ToString();
            }
            else if (argStr == "--label" && i + 1 < context.Arguments.Count)
            {
                label = context.Arguments[++i]?.ToString();
            }
            else if (argStr == "--color" && i + 1 < context.Arguments.Count)
            {
                color = PlotColorExtensions.ParseColor(context.Arguments[++i]?.ToString());
            }
            else if (argStr == "--save" && i + 1 < context.Arguments.Count)
            {
                savePath = context.Arguments[++i]?.ToString();
            }
            else
            {
                positionalArgs.Add(context.Arguments[i]);
            }
        }

        var xVals = new List<double>();
        var yVals = new List<double>();

        // Check piped input
        var pipedItems = new List<object?>();
        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            pipedItems.Add(item);
        }

        if (pipedItems.Count > 0)
        {
            foreach (var item in pipedItems)
            {
                ExtractPoint(item, xVals, yVals);
            }
        }
        else if (positionalArgs.Count >= 2)
        {
            ExtractValues(positionalArgs[0], xVals);
            ExtractValues(positionalArgs[1], yVals);
        }
        else if (positionalArgs.Count == 1)
        {
            ExtractValues(positionalArgs[0], yVals);
        }

        if (yVals.Count == 0)
        {
            throw new InvalidOperationException("plot requires at least one data point to display.");
        }

        if (xVals.Count == 0)
        {
            xVals = Enumerable.Range(0, yVals.Count).Select(i => (double)i).ToList();
        }

        var fig = new Figure(title: title);
        fig.Plot(xVals, yVals, label: label, color: color);

        if (!string.IsNullOrEmpty(savePath))
        {
            fig.SaveSvg(savePath);
        }

        yield return fig;
    }

    private static void ExtractPoint(object? item, List<double> xs, List<double> ys)
    {
        if (item is null) return;
        if (item is IDictionary<string, object?> dict)
        {
            if (dict.TryGetValue("x", out var ox) && dict.TryGetValue("y", out var oy))
            {
                xs.Add(ToDouble(ox));
                ys.Add(ToDouble(oy));
                return;
            }
        }
        if (item is System.Collections.IList list && list.Count >= 2)
        {
            xs.Add(ToDouble(list[0]));
            ys.Add(ToDouble(list[1]));
            return;
        }

        ys.Add(ToDouble(item));
    }

    private static void ExtractValues(object? val, List<double> target)
    {
        if (val is null) return;
        if (val is System.Collections.IEnumerable enumerable and not string)
        {
            foreach (var elem in enumerable)
            {
                target.Add(ToDouble(elem));
            }
        }
        else
        {
            target.Add(ToDouble(val));
        }
    }

    private static double ToDouble(object? val) => val switch
    {
        double d => d,
        float f => f,
        int i => i,
        long l => l,
        decimal m => (double)m,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => Convert.ToDouble(val, CultureInfo.InvariantCulture)
    };
}
