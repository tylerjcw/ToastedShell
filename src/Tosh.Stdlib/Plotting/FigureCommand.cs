using Tosh.Runtime;

namespace Tosh.Stdlib.Plotting;

[CommandCategory("Plotting")]
[CommandExample("figure --width 1000 --height 600", Title = "Create a custom figure")]
[CommandOutput("A newly configured Figure instance.", ClrType = typeof(IAsyncEnumerable<Figure>))]
public sealed class FigureCommand : ShellCommand
{
    public FigureCommand()
        : base("figure", "Creates or configures a new Figure instance.", "figure [--title <text>] [--width <px>] [--height <px>] [--dark]") { }

    public override IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        string? title = null;
        int width = 800;
        int height = 500;
        PlotTheme theme = PlotTheme.Light;

        for (int i = 0; i < context.Arguments.Count; i++)
        {
            var arg = context.Arguments[i]?.ToString() ?? "";
            if (arg == "--title" && i + 1 < context.Arguments.Count) title = context.Arguments[++i]?.ToString();
            else if (arg == "--width" && i + 1 < context.Arguments.Count && int.TryParse(context.Arguments[++i]?.ToString(), out var w)) width = w;
            else if (arg == "--height" && i + 1 < context.Arguments.Count && int.TryParse(context.Arguments[++i]?.ToString(), out var h)) height = h;
            else if (arg == "--dark") theme = PlotTheme.Dark;
        }

        var fig = new Figure(width, height, title, theme);
        return ReturnSingle(fig);
    }

    private static async IAsyncEnumerable<object?> ReturnSingle(Figure fig)
    {
        yield return fig;
    }
}
