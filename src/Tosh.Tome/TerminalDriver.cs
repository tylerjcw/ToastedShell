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
    // SGR (1006) extended mouse encoding, plus button-event tracking (1002)
    // so we get drags and wheel events. 1000 enables press/release.
    private const string EnableMouseSeq = "\u001b[?1000h\u001b[?1002h\u001b[?1006h";
    private const string DisableMouseSeq = "\u001b[?1006l\u001b[?1002l\u001b[?1000l";

    private readonly bool _mouseEnabled;
    private bool _disposed;

    public TerminalDriver(bool enableMouse = true)
    {
        Console.Write(EnterAlternateScreenSeq);
        Console.Write(HideCursorSeq);
        Console.Write(ClearScreenSeq);
        Console.Write(HomeSeq);
        _mouseEnabled = enableMouse && Environment.GetEnvironmentVariable("TOME_NO_MOUSE") != "1";
        if (_mouseEnabled) Console.Write(EnableMouseSeq);
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

    /// <summary>Moves and shows the hardware cursor. Coordinates are zero-based.</summary>
    public void ShowCursorAt(int row, int column)
    {
        MoveCursor(row, column);
        Console.Write(ShowCursorSeq);
    }

    public void HideCursor() => Console.Write(HideCursorSeq);

    public void Flush() => Console.Out.Flush();

    public ConsoleKeyInfo ReadKey()
    {
        // Backwards-compatible accessor: discard mouse events and only
        // return real key presses. Used by modal prompts that don't care
        // about pointer input.
        while (true)
        {
            var evt = ReadEvent();
            if (evt.Kind == InputEventKind.Key) return evt.Key;
        }
    }

    /// <summary>
    /// Reads a single input event, parsing SGR mouse sequences out of the
    /// stdin stream when mouse mode is enabled. Falls back to a plain key
    /// event for anything that doesn't look like a mouse report.
    /// </summary>
    public InputEvent ReadEvent()
    {
        var first = Console.ReadKey(intercept: true);
        if (!_mouseEnabled || first.Key != ConsoleKey.Escape || !Console.KeyAvailable)
            return InputEvent.FromKey(first);

        // Peek the next two bytes — a mouse report always starts with "[<".
        // If anything else follows the Escape we treat the lead Escape as
        // a real key press and replay nothing (cheap and good enough for
        // the modal "Esc cancels" semantics every prompt expects).
        var second = Console.ReadKey(intercept: true);
        if (second.KeyChar != '[' || !Console.KeyAvailable)
            return InputEvent.FromKey(first);
        var third = Console.ReadKey(intercept: true);
        if (third.KeyChar != '<')
            return InputEvent.FromKey(first);

        // Drain the rest of the sequence: digits and ';' separators
        // terminated by 'M' (press / motion) or 'm' (release).
        var sb = new System.Text.StringBuilder();
        char terminator = '\0';
        while (Console.KeyAvailable)
        {
            var c = Console.ReadKey(intercept: true).KeyChar;
            if (c == 'M' || c == 'm') { terminator = c; break; }
            sb.Append(c);
        }
        if (terminator == '\0') return InputEvent.FromKey(first);

        var parts = sb.ToString().Split(';');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], out var cb) ||
            !int.TryParse(parts[1], out var cx) ||
            !int.TryParse(parts[2], out var cy))
        {
            return InputEvent.FromKey(first);
        }

        // SGR encoding: bits 0..1 = button, 2 = shift, 3 = alt, 4 = ctrl,
        // 5 = motion (drag while button held), 6 = wheel.
        var shift = (cb & 0x04) != 0;
        var alt = (cb & 0x08) != 0;
        var ctrl = (cb & 0x10) != 0;
        var motion = (cb & 0x20) != 0;
        var wheel = (cb & 0x40) != 0;
        var buttonBits = cb & 0x03;

        var row = Math.Max(0, cy - 1);
        var col = Math.Max(0, cx - 1);

        if (wheel)
        {
            // 64 = wheel up, 65 = wheel down. Sign matches scroll-line delta:
            // wheel-up reduces ScrollLine, wheel-down increases.
            var delta = buttonBits == 0 ? +1 : -1;
            return new InputEvent(InputEventKind.MouseWheel, default, MouseButton.None,
                row, col, delta, shift, alt, ctrl);
        }

        var button = buttonBits switch
        {
            0 => MouseButton.Left,
            1 => MouseButton.Middle,
            2 => MouseButton.Right,
            _ => MouseButton.None,
        };

        InputEventKind kind;
        if (motion) kind = InputEventKind.MouseMove;
        else if (terminator == 'M') kind = InputEventKind.MousePress;
        else kind = InputEventKind.MouseRelease;

        return new InputEvent(kind, default, button, row, col, 0, shift, alt, ctrl);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_mouseEnabled) Console.Write(DisableMouseSeq);
        Console.Write(ShowCursorSeq);
        Console.Write(ExitAlternateScreenSeq);
        Console.Out.Flush();
    }
}
