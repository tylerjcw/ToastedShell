namespace Tosh.Cli.Tui;

internal interface ITuiHost
{
    bool IsInteractive { get; }

    TuiSize? TryGetSize();

    ConsoleKeyInfo ReadKey(bool intercept = true);

    bool TryReadPendingKey(out ConsoleKeyInfo key, bool intercept = true);

    TuiInputEvent ReadInput();

    bool TryReadPendingInput(out TuiInputEvent inputEvent);

    void Write(string text);
}
