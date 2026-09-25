using System.Drawing;
using System.Globalization;
using Tosh.Runtime;

namespace Tosh.Stdlib.Plotting;

[CommandCategory("Plotting")]
[CommandExample("bar ['A', 'B', 'C'] [10, 25, 18]", Title = "Create a categorical bar chart")]
[CommandOutput("A Figure containing a categorical bar series.", ClrType = typeof(IAsyncEnumerable<Figure>))]
public sealed class BarCommand : ShellCommand
{
    public BarCommand()
        : base("bar", "Generates a categorical bar chart.", "bar [categories] [values] [--title <text>] [--color <color>]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        string? title = null;
        Color? color = null;
        var positionalArgs = new List<object?>();

        for (int i = 0; i < context.Arguments.Count; i++)
        {
            var argStr = context.Arguments[i]?.ToString() ?? "";
            if (argStr == "--title" && i + 1 < context.Arguments.Count) title = context.Arguments[++i]?.ToString();
            else if (argStr == "--color" && i + 1 < context.Arguments.Count) color = PlotColorExtensions.ParseColor(context.Arguments[++i]?.ToString());
            else positionalArgs.Add(context.Arguments[i]);
        }

        var cats = new List<string>();
        var vals = new List<double>();

        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            if (item is IDictionary<string, object?> dict)
            {
                var catKey = dict.Keys.FirstOrDefault(k => k.Equals("name", StringComparison.OrdinalIgnoreCase) || k.Equals("category", StringComparison.OrdinalIgnoreCase) || k.Equals("label", StringComparison.OrdinalIgnoreCase)) ?? dict.Keys.First();
                var valKey = dict.Keys.FirstOrDefault(k => k.Equals("value", StringComparison.OrdinalIgnoreCase) || k.Equals("count", StringComparison.OrdinalIgnoreCase) || k.Equals("size", StringComparison.OrdinalIgnoreCase)) ?? dict.Keys.Last();
                cats.Add(dict[catKey]?.ToString() ?? "");
                vals.Add(Convert.ToDouble(dict[valKey], CultureInfo.InvariantCulture));
            }
            else if (item is System.Collections.IList list && list.Count >= 2)
            {
                cats.Add(list[0]?.ToString() ?? "");
                vals.Add(Convert.ToDouble(list[1], CultureInfo.InvariantCulture));
            }
        }

        if (cats.Count == 0 && positionalArgs.Count >= 2)
        {
            if (positionalArgs[0] is System.Collections.IEnumerable cEnum)
                foreach (var c in cEnum) cats.Add(c?.ToString() ?? "");
            if (positionalArgs[1] is System.Collections.IEnumerable vEnum)
                foreach (var v in vEnum) vals.Add(Convert.ToDouble(v, CultureInfo.InvariantCulture));
        }

        if (vals.Count == 0) throw new InvalidOperationException("bar requires categories and values.");

        var fig = new Figure(title: title);
        fig.Bar(cats, vals, color: color);
        yield return fig;
    }
}
