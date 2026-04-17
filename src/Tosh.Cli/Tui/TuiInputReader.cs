using System.Text;

namespace Tosh.Cli.Tui;

/// <summary>
/// Reads raw bytes from stdin and decodes them into <see cref="TuiInputEvent"/> values,
/// handling both regular key presses (via <see cref="Console.ReadKey"/>) and SGR extended
/// mouse protocol escape sequences (<c>CSI &lt; Pb ; Pc ; Pr M/m</c>).
/// </summary>
internal sealed class TuiInputReader
{
    /// <summary>Escape sequence introducer for SGR mouse: <c>\x1b[&lt;</c>.</summary>
    private const char Escape = '\x1b';

    private readonly Queue<TuiInputEvent> _pendingEvents = new();

    /// <summary>
    /// Blocking read: returns the next input event. If a previous call buffered
    /// extra events (e.g. an escape sequence that was not a mouse event), those
    /// are returned first before reading new input from the console.
    /// </summary>
    public TuiInputEvent Read()
    {
        if (_pendingEvents.TryDequeue(out var buffered))
        {
            return buffered;
        }

        var key = Console.ReadKey(intercept: true);

        if (key.Key == ConsoleKey.Escape && Console.KeyAvailable)
        {
            return TryReadMouseSequence(key);
        }

        return TuiInputEvent.FromKey(key);
    }

    /// <summary>
    /// Non-blocking: returns true and sets <paramref name="inputEvent"/> if an event
    /// is available without waiting.
    /// </summary>
    public bool TryReadPending(out TuiInputEvent inputEvent)
    {
        if (_pendingEvents.TryDequeue(out inputEvent))
        {
            return true;
        }

        if (!Console.KeyAvailable)
        {
            inputEvent = default;
            return false;
        }

        var key = Console.ReadKey(intercept: true);

        if (key.Key == ConsoleKey.Escape && Console.KeyAvailable)
        {
            inputEvent = TryReadMouseSequence(key);
            return true;
        }

        inputEvent = TuiInputEvent.FromKey(key);
        return true;
    }

    /// <summary>
    /// Attempt to decode an SGR mouse sequence after seeing ESC.
    /// SGR format: <c>ESC [ &lt; Pb ; Pc ; Pr M</c> (press) or <c>ESC [ &lt; Pb ; Pc ; Pr m</c> (release).
    /// If the sequence doesn't match, the consumed characters are re-enqueued as key events.
    /// </summary>
    private TuiInputEvent TryReadMouseSequence(ConsoleKeyInfo escapeKey)
    {
        var buffer = new StringBuilder();
        buffer.Append(Escape);

        // We need: [ < Pb ; Pc ; Pr M/m
        // Read character-by-character, checking for early bail.
        // Maximum reasonable length: ESC [ < nnn ; nnn ; nnn M = ~20 chars

        const int maxSequenceLength = 32;

        for (var i = 0; i < maxSequenceLength; i++)
        {
            if (!Console.KeyAvailable)
            {
                break;
            }

            var next = Console.ReadKey(intercept: true);
            buffer.Append(next.KeyChar);

            // Check for terminal character M (press/drag) or m (release)
            if (next.KeyChar is 'M' or 'm' && buffer.Length >= 7)
            {
                if (TryParseSgrMouse(buffer.ToString(), out var mouseEvent))
                {
                    return TuiInputEvent.FromMouse(mouseEvent);
                }

                break;
            }

            // Valid SGR chars: [ < 0-9 ; M m
            if (!IsSgrSequenceChar(next.KeyChar, i))
            {
                break;
            }
        }

        // Not a mouse sequence — return the original Escape key.
        // Additional consumed characters become pending key events.
        for (var i = 1; i < buffer.Length; i++)
        {
            var ch = buffer[i];
            _pendingEvents.Enqueue(TuiInputEvent.FromKey(
                new ConsoleKeyInfo(ch, CharToConsoleKey(ch), false, false, false)));
        }

        return TuiInputEvent.FromKey(escapeKey);
    }

    private static bool IsSgrSequenceChar(char c, int positionAfterEscape) =>
        positionAfterEscape switch
        {
            0 => c == '[',
            1 => c == '<',
            _ => char.IsAsciiDigit(c) || c == ';',
        };

    /// <summary>
    /// Parse a full SGR mouse sequence string: <c>\x1b[&lt;Pb;Pc;PrM</c> or <c>\x1b[&lt;Pb;Pc;Prm</c>.
    /// </summary>
    internal static bool TryParseSgrMouse(string sequence, out TuiMouseEvent result)
    {
        result = default;

        // Minimum: ESC [ < b ; c ; r M → 7 chars
        if (sequence.Length < 7 || sequence[0] != Escape || sequence[1] != '[' || sequence[2] != '<')
        {
            return false;
        }

        var terminator = sequence[^1];

        if (terminator is not ('M' or 'm'))
        {
            return false;
        }

        // Parse "Pb;Pc;Pr" from sequence[3..^1]
        var payload = sequence.AsSpan(3, sequence.Length - 4);
        Span<Range> parts = stackalloc Range[4];
        var count = payload.Split(parts, ';');

        if (count != 3)
        {
            return false;
        }

        if (!int.TryParse(payload[parts[0]], out var buttonCode) ||
            !int.TryParse(payload[parts[1]], out var column) ||
            !int.TryParse(payload[parts[2]], out var row))
        {
            return false;
        }

        // SGR coordinates are 1-based; convert to 0-based.
        column = Math.Max(0, column - 1);
        row = Math.Max(0, row - 1);

        var shift = (buttonCode & 4) != 0;
        var alt = (buttonCode & 8) != 0;
        var control = (buttonCode & 16) != 0;
        var isDrag = (buttonCode & 32) != 0;
        var isRelease = terminator == 'm';

        var baseButton = buttonCode & 3;
        var isScroll = (buttonCode & 64) != 0;

        TuiMouseButton button;
        TuiMouseAction action;

        if (isScroll)
        {
            button = baseButton == 0 ? TuiMouseButton.ScrollUp : TuiMouseButton.ScrollDown;
            action = TuiMouseAction.Scroll;
        }
        else
        {
            button = baseButton switch
            {
                0 => TuiMouseButton.Left,
                1 => TuiMouseButton.Middle,
                2 => TuiMouseButton.Right,
                _ => TuiMouseButton.None,
            };

            if (isRelease)
            {
                action = TuiMouseAction.Release;
            }
            else if (isDrag)
            {
                action = TuiMouseAction.Drag;
            }
            else
            {
                action = TuiMouseAction.Press;
            }
        }

        result = new TuiMouseEvent(action, button, column, row, shift, alt, control);
        return true;
    }

    private static ConsoleKey CharToConsoleKey(char c) =>
        c switch
        {
            '[' => ConsoleKey.Oem4,
            '<' => ConsoleKey.OemComma,
            ';' => ConsoleKey.Oem1,
            >= '0' and <= '9' => ConsoleKey.D0 + (c - '0'),
            _ => ConsoleKey.NoName,
        };
}
