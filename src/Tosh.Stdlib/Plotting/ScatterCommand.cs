using System.Drawing;
using System.Globalization;
using Tosh.Runtime;

namespace Tosh.Stdlib.Plotting;

[CommandCategory("Plotting")]
[CommandArgument("x", "Data values for X axis.", Required = false)]
[CommandArgument("y", "Data values for Y axis.", Required = false)]
[CommandExample("scatter [1, 2, 3] [4, 5, 6]", Title = "Create a scatter plot")]
[CommandOutput("A Figure with a scatter series.", ClrType = typeof(IAsyncEnumerable<Figure>))]
public sealed class ScatterCommand : ShellCommand
{
    public ScatterCommand()
        : base("scatter", "Generates a scatter plot from coordinates, lists, or piped data.", "scatter [x] [y] [--title <text>] [--size <n>] [--color <color>]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        string? title = null;
        string? label = null;
        Color? color = null;
        float size = 5f;
        var positionalArgs = new List<object?>();

        for (int i = 0; i < context.Arguments.Count; i++)
        {
            var argStr = context.Arguments[i]?.ToString() ?? "";
            if (argStr == "--title" && i + 1 < context.Arguments.Count) title = context.Arguments[++i]?.ToString();
            else if (argStr == "--label" && i + 1 < context.Arguments.Count) label = context.Arguments[++i]?.ToString();
            else if (argStr == "--color" && i + 1 < context.Arguments.Count) color = PlotColorExtensions.ParseColor(context.Arguments[++i]?.ToString());
            else if (argStr == "--size" && i + 1 < context.Arguments.Count && float.TryParse(context.Arguments[++i]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) size = s;
            else positionalArgs.Add(context.Arguments[i]);
        }

        var xVals = new List<double>();
        var yVals = new List<double>();

        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            if (item is System.Collections.IList list && list.Count >= 2)
            {
                xVals.Add(Convert.ToDouble(list[0], CultureInfo.InvariantCulture));
                yVals.Add(Convert.ToDouble(list[1], CultureInfo.InvariantCulture));
            }
            else
            {
                yVals.Add(Convert.ToDouble(item, CultureInfo.InvariantCulture));
            }
        }

        if (xVals.Count == 0 && positionalArgs.Count >= 2)
        {
            if (positionalArgs[0] is System.Collections.IEnumerable xEnum)
                foreach (var x in xEnum) xVals.Add(Convert.ToDouble(x, CultureInfo.InvariantCulture));
            if (positionalArgs[1] is System.Collections.IEnumerable yEnum)
                foreach (var y in yEnum) yVals.Add(Convert.ToDouble(y, CultureInfo.InvariantCulture));
        }

        if (xVals.Count == 0 && yVals.Count > 0)
        {
            xVals = Enumerable.Range(0, yVals.Count).Select(i => (double)i).ToList();
        }

        if (yVals.Count == 0) throw new InvalidOperationException("scatter requires data points to plot.");

        var fig = new Figure(title: title);
        fig.Scatter(xVals, yVals, label: label, color: color, size: size);
        yield return fig;
    }
}
