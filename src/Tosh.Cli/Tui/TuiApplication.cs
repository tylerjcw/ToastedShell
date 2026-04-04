namespace Tosh.Cli.Tui;

internal static class TuiApplication
{
    private const string EnterAlternateScreen = "\u001b[?1049h";
    private const string ExitAlternateScreen = "\u001b[?1049l";
    private const string HideCursor = "\u001b[?25l";
    private const string ShowCursor = "\u001b[?25h";
    private const string ClearScreenAndHome = "\u001b[2J\u001b[H";
    private const int MaxInputBurst = 512;

    public static void Run(ITuiHost host, ITuiScreen screen)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(screen);

        if (!host.IsInteractive)
        {
            throw new InvalidOperationException("This interactive browser requires a real terminal.");
        }

        host.Write(EnterAlternateScreen);
        host.Write(HideCursor);

        try
        {
            while (true)
            {
                var size = host.TryGetSize() ?? new TuiSize(80, 25);
                var frame = screen.Render(size);
                host.Write(ClearScreenAndHome);
                host.Write(frame.Content);

                var key = host.ReadKey(intercept: true);

                if (ProcessInputBatch(host, screen, key) == TuiScreenResult.Exit)
                {
                    break;
                }
            }
        }
        finally
        {
            host.Write(ShowCursor);
            host.Write(ExitAlternateScreen);
        }
    }

    internal static TuiScreenResult ProcessInputBatch(
        ITuiHost host,
        ITuiScreen screen,
        ConsoleKeyInfo firstKey,
        int maxAdditionalKeys = MaxInputBurst)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(screen);

        if (screen.HandleKey(firstKey) == TuiScreenResult.Exit)
        {
            return TuiScreenResult.Exit;
        }

        for (var index = 0; index < maxAdditionalKeys; index += 1)
        {
            if (!host.TryReadPendingKey(out var pendingKey, intercept: true))
            {
                break;
            }

            if (screen.HandleKey(pendingKey) == TuiScreenResult.Exit)
            {
                return TuiScreenResult.Exit;
            }
        }

        return TuiScreenResult.Continue;
    }
}
