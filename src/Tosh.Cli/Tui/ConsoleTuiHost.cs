namespace Tosh.Cli.Tui;

internal sealed class ConsoleTuiHost : ITuiHost
{
    public bool IsInteractive => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    public TuiSize? TryGetSize()
    {
        try
        {
            if (!IsInteractive)
            {
                return null;
            }

            var width = Console.WindowWidth;
            var height = Console.WindowHeight;

            return width > 0 && height > 0
                ? new TuiSize(width, height)
                : null;
        }
        catch
        {
            return null;
        }
    }

    public ConsoleKeyInfo ReadKey(bool intercept = true)
    {
        return Console.ReadKey(intercept);
    }

    public bool TryReadPendingKey(out ConsoleKeyInfo key, bool intercept = true)
    {
        if (!Console.KeyAvailable)
        {
            key = default;
            return false;
        }

        key = Console.ReadKey(intercept);
        return true;
    }

    public void Write(string text)
    {
        Console.Write(text);
    }
}
