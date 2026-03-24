using Tosh.Core;

namespace Tosh.Cli;

internal static class ConsoleDisplay
{
    public static DisplayRenderOptions CreateRenderOptions(ToshRuntime runtime)
    {
        return new DisplayRenderOptions(runtime.Display.Style, TryGetAvailableWidth());
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
}
