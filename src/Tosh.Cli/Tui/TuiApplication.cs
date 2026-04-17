namespace Tosh.Cli.Tui;

internal static class TuiApplication
{
    private const string EnterAlternateScreen = "\u001b[?1049h";
    private const string ExitAlternateScreen = "\u001b[?1049l";
    private const string HideCursor = "\u001b[?25l";
    private const string ShowCursor = "\u001b[?25h";
    private const string ClearScreenAndHome = "\u001b[2J\u001b[H";
    private const string EnableSgrMouse = "\u001b[?1000h\u001b[?1006h";
    private const string DisableSgrMouse = "\u001b[?1000l\u001b[?1006l";
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
        host.Write(EnableSgrMouse);

        try
        {
            while (true)
            {
                var size = host.TryGetSize() ?? new TuiSize(80, 25);
                var frame = screen.Render(size);
                host.Write(ClearScreenAndHome);
                host.Write(frame.Content);

                var firstInput = host.ReadInput();

                if (ProcessInputBatch(host, screen, firstInput) == TuiScreenResult.Exit)
                {
                    break;
                }
            }
        }
        finally
        {
            host.Write(DisableSgrMouse);
            host.Write(ShowCursor);
            host.Write(ExitAlternateScreen);
        }
    }

    internal static TuiScreenResult ProcessInputBatch(
        ITuiHost host,
        ITuiScreen screen,
        TuiInputEvent firstInput,
        int maxAdditionalInputs = MaxInputBurst)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(screen);

        if (screen.HandleInput(firstInput) == TuiScreenResult.Exit)
        {
            return TuiScreenResult.Exit;
        }

        for (var index = 0; index < maxAdditionalInputs; index += 1)
        {
            if (!host.TryReadPendingInput(out var pendingInput))
            {
                break;
            }

            if (screen.HandleInput(pendingInput) == TuiScreenResult.Exit)
            {
                return TuiScreenResult.Exit;
            }
        }

        return TuiScreenResult.Continue;
    }
}
