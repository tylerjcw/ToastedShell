using Tosh.Runtime;

namespace Tosh.Cli;

internal static class ConsoleDisplay
{
    public static DisplayRenderOptions CreateRenderOptions(ToshRuntime runtime)
    {
        return new DisplayRenderOptions(
            runtime.Display.Style,
            TryGetAvailableWidth(),
            TryGetAvailableHeight(),
            ColumnSelectionResolver: runtime.GetDisplaySelection);
    }

    public static async Task WriteRenderedAsync(string rendered, ToshRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        if (string.IsNullOrEmpty(rendered))
        {
            return;
        }

        var availableHeight = TryGetAvailableHeight();

        if (ConsolePager.ShouldPage(rendered, availableHeight, runtime.Config.Display.Paging, Console.IsOutputRedirected))
        {
            await ConsolePager.WriteAsync(rendered, availableHeight, runtime.Config.Display.Paging, runtime.Config.Theme.Completion.Footer);
            return;
        }

        await Console.Out.WriteLineAsync(rendered);
    }

    private static int? TryGetAvailableWidth()
    {
        try
        {
            if (Console.IsOutputRedirected)
            {
                return null;
            }

            return Console.WindowWidth > 1 ? Console.WindowWidth - 1 : null;
        }
        catch
        {
            return null;
        }
    }

    private static int? TryGetAvailableHeight()
    {
        try
        {
            if (Console.IsOutputRedirected)
            {
                return null;
            }

            return Console.WindowHeight > 1 ? Console.WindowHeight - 1 : null;
        }
        catch
        {
            return null;
        }
    }
}
