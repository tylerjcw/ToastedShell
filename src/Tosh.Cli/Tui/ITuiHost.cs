namespace Tosh.Cli.Tui;

internal interface ITuiHost
{
    bool IsInteractive { get; }

    TuiSize? TryGetSize();

    ConsoleKeyInfo ReadKey(bool intercept = true);

    bool TryReadPendingKey(out ConsoleKeyInfo key, bool intercept = true);

    void Write(string text);
}
