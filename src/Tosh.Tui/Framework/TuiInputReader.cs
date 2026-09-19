using System.Text;

namespace Tosh.Tui;

/// <summary>
/// Reads raw bytes from stdin and decodes them into <see cref="TuiInputEvent"/> values,
/// handling both regular key presses (via <see cref="Console.ReadKey"/>) and SGR extended
/// mouse protocol escape sequences (<c>CSI &lt; Pb ; Pc ; Pr M/m</c>).
/// </summary>
public sealed class TuiInputReader
{
    /// <summary>Escape sequence introducer for SGR mouse: <c>\x1b[&lt;</c>.</summary>
    private const char Escape = '\x1b';

    private readonly Queue<TuiInputEvent> _pendingEvents = new();

    /// <summary>
    /// Blocking read: returns the next input event. If a previous call buffered
    /// extra events (e.g. an escape sequence that was not a mouse event), those
    /// are returned first before reading new input from the console.
    /// </summary>
    /// <summary>Puts an event at the back of the queue, to be read before the console is.</summary>
    /// <remarks>
    /// The seam a test drives a prompt through. The queue is already how a half-read escape
    /// sequence is handed back, so this adds a door rather than a mechanism.
    /// </remarks>
    internal void Enqueue(TuiInputEvent input) => _pendingEvents.Enqueue(input);

    public TuiInputEvent Read()
    {
        if (_pendingEvents.TryDequeue(out var buffered))
        {
            return buffered;
        }

        var key = Console.ReadKey(intercept: true);

        if (key.Key == ConsoleKey.Escape && Console.KeyAvailable)
        {
            return TryReadPaste(out var pasted) ? pasted : TryReadMouseSequence(key);
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
            inputEvent = TryReadPaste(out var pasted) ? pasted : TryReadMouseSequence(key);
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
    /// <summary>
    /// Reads a bracketed paste, if that is what this escape begins.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A terminal asked to bracket pastes wraps them in <c>CSI 200 ~</c> and
    /// <c>CSI 201 ~</c> — see <see cref="Rendering.TuiBracketedPaste"/>. Read whole, the
    /// newlines inside are characters rather than presses of Enter, which is the difference
    /// between pasting three lines into a field and submitting the form three times.
    /// </para>
    /// <para>
    /// Tried before the mouse sequence because the two share their first two characters and
    /// the mouse parser would reject <c>2</c> where it wants <c>&lt;</c>, handing the rest
    /// back as keystrokes — <c>[200~</c> typed into the field.
    /// </para>
    /// <para>
    /// An unterminated paste — the terminal was interrupted, or never sent the closing
    /// bracket — yields what arrived rather than waiting for a bracket that is not coming.
    /// </para>
    /// </remarks>
    private bool TryReadPaste(out TuiInputEvent pasted)
    {
        pasted = default;

        // `[200~`, already knowing an Escape was read.
        const string Opening = "[200~";
        var seen = new StringBuilder();

        for (var index = 0; index < Opening.Length; index++)
        {
            if (!Console.KeyAvailable)
            {
                Replay(seen);
                return false;
            }

            var next = Console.ReadKey(intercept: true);
            seen.Append(next.KeyChar);

            if (next.KeyChar != Opening[index])
            {
                Replay(seen);
                return false;
            }
        }

        var text = new StringBuilder();
        var terminator = new StringBuilder();

        while (Console.KeyAvailable)
        {
            var next = Console.ReadKey(intercept: true);

            // The closing bracket is matched as it arrives, so its characters never reach
            // the text. A partial match that breaks off is text after all.
            var expected = TuiBracketedPasteClose[terminator.Length];

            if (next.KeyChar == expected)
            {
                terminator.Append(next.KeyChar);

                if (terminator.Length == TuiBracketedPasteClose.Length)
                {
                    pasted = TuiInputEvent.FromPaste(text.ToString());
                    return true;
                }

                continue;
            }

            text.Append(terminator);
            terminator.Clear();
            text.Append(next.KeyChar);
        }

        // Ran out before the closing bracket. What arrived is still a paste.
        text.Append(terminator);
        pasted = TuiInputEvent.FromPaste(text.ToString());
        return true;
    }

    /// <summary>`ESC [ 2 0 1 ~`, without the escape that has already been matched.</summary>
    private const string TuiBracketedPasteClose = "\x1b[201~";

    /// <summary>Hands characters back as keystrokes, in the order they were read.</summary>
    private void Replay(StringBuilder consumed)
    {
        foreach (var character in consumed.ToString())
        {
            _pendingEvents.Enqueue(TuiInputEvent.FromKey(
                new ConsoleKeyInfo(character, CharToConsoleKey(character), false, false, false)));
        }
    }

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
    public static bool TryParseSgrMouse(string sequence, out TuiMouseEvent result)
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
