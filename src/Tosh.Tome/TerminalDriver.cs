namespace Tosh.Tome;

/// <summary>
/// Owns the terminal: alternate screen buffer, cursor visibility, raw input mode.
/// Restores everything on dispose so the parent shell is unaffected.
/// </summary>
internal sealed class TerminalDriver : IDisposable
{
    private const string EnterAlternateScreenSeq = "\u001b[?1049h";
    private const string ExitAlternateScreenSeq = "\u001b[?1049l";
    private const string HideCursorSeq = "\u001b[?25l";
    private const string ShowCursorSeq = "\u001b[?25h";
    private const string ClearScreenSeq = "\u001b[2J";
    private const string HomeSeq = "\u001b[H";

    private bool _disposed;

    public TerminalDriver()
    {
        Console.Write(EnterAlternateScreenSeq);
        Console.Write(HideCursorSeq);
        Console.Write(ClearScreenSeq);
        Console.Write(HomeSeq);
        Console.Out.Flush();
    }

    public int Width => Math.Max(20, Console.WindowWidth);

    public int Height => Math.Max(5, Console.WindowHeight);

    public void Clear()
    {
        Console.Write(ClearScreenSeq);
        Console.Write(HomeSeq);
    }

    public void MoveCursor(int row, int column)
    {
        // ANSI is 1-indexed.
        Console.Write($"\u001b[{row + 1};{column + 1}H");
    }

    public void Write(string text) => Console.Write(text);

    public void ShowCursorAt(int row, int column)
    {
        MoveCursor(row, column);
        Console.Write(ShowCursorSeq);
    }

    public void HideCursor() => Console.Write(HideCursorSeq);

    public void Flush() => Console.Out.Flush();

    public ConsoleKeyInfo ReadKey() => Console.ReadKey(intercept: true);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Console.Write(ShowCursorSeq);
        Console.Write(ExitAlternateScreenSeq);
        Console.Out.Flush();
    }
}
