using Tosh.Runtime;

namespace Tosh.Stdlib.Plotting;

[CommandCategory("Plotting")]
[CommandArgument("file", "Destination path (.svg or .html).")]
[CommandExample("plot [1, 4, 9] | save-plot output.svg", Title = "Save a plot to SVG")]
[CommandOutput("Confirmation message of the saved plot file path.", ClrType = typeof(IAsyncEnumerable<string>))]
public sealed class SavePlotCommand : ShellCommand
{
    public SavePlotCommand()
        : base("save-plot", "Saves a Figure to an SVG or HTML file.", "save-plot <file.svg|file.html> [figure]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count == 0)
        {
            throw new InvalidOperationException("save-plot requires a destination file path.");
        }

        var destination = context.Arguments[0]?.ToString() ?? throw new InvalidOperationException("save-plot: invalid file path.");
        Figure? targetFig = null;

        if (context.Arguments.Count >= 2 && context.Arguments[1] is Figure figArg)
        {
            targetFig = figArg;
        }
        else
        {
            await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
            {
                if (item is Figure f)
                {
                    targetFig = f;
                    break;
                }
            }
        }

        if (targetFig is null)
        {
            throw new InvalidOperationException("save-plot requires a Figure to save.");
        }

        if (destination.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            targetFig.SaveHtml(destination);
        }
        else
        {
            targetFig.SaveSvg(destination);
        }

        yield return $"Saved plot to {destination}";
    }
}
