using Tosh.Tui.Rendering;
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

        if (key.Key == ConsoleKey.Escape && SequenceFollows())
        {
            return ReadAfterEscape(key);
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

        if (key.Key == ConsoleKey.Escape && SequenceFollows())
        {
            inputEvent = ReadAfterEscape(key);
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
    /// Decides what an escape begins, having already read the escape itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every sequence the reader understands starts <c>ESC [</c> and is told apart by the
    /// character after it: <c>I</c> and <c>O</c> are focus reports, <c>&lt;</c> is an SGR
    /// mouse report, and <c>2</c> begins a bracketed paste. One place reads that character,
    /// because each parser used to be tried in turn and a failed attempt had already
    /// consumed the bracket the next one needed.
    /// </para>
    /// <para>
    /// Anything else is a real Escape followed by whatever was typed after it, which is
    /// handed back in the order it arrived.
    /// </para>
    /// </remarks>
    private TuiInputEvent ReadAfterEscape(ConsoleKeyInfo escapeKey)
    {
        var seen = new StringBuilder();

        if (!TryTake(seen, out var bracket) || bracket != '[')
        {
            Replay(seen);
            return TuiInputEvent.FromKey(escapeKey);
        }

        if (!TryTake(seen, out var introducer))
        {
            Replay(seen);
            return TuiInputEvent.FromKey(escapeKey);
        }

        switch (introducer)
        {
            // `CSI I` and `CSI O` — the terminal's window gained or lost focus.
            case 'I':
                return TuiInputEvent.FromFocus(true);

            case 'O':
                return TuiInputEvent.FromFocus(false);

            case '<':
                return ReadSgrMouse(escapeKey, seen);

            default:
                // Digits begin a parameterised sequence, and which one it is depends on the
                // final byte rather than the first digit: `200~` opens a paste, `13;5u` is a
                // key report. Branching on the digit alone got that wrong the moment a
                // second digit-led sequence existed.
                if (char.IsAsciiDigit(introducer))
                {
                    return ReadParameterised(escapeKey, seen, introducer);
                }

                // `CSI A`..`CSI D` are the arrow keys, and the rest of this table is the
                // other keys a terminal spells the same way. They used to fall through to
                // the Escape below, so every arrow press read as Escape — which closes a
                // screen that has no form to cancel.
                if (CsiFinalKey(introducer) is { } named)
                {
                    return FromNamedKey(named, introducer == 'Z' ? ConsoleModifiers.Shift : 0);
                }

                Replay(seen);
                return TuiInputEvent.FromKey(escapeKey);
        }
    }

    /// <summary>The key a CSI final byte names, or null when it names none.</summary>
    /// <remarks>
    /// .NET decodes these itself when it is reading the terminal in its own mode. Asking for
    /// the Kitty protocol's "disambiguate escape codes" is what stops it: the terminal then
    /// reports keys in a form .NET's terminfo does not match, so they arrive here raw.
    /// </remarks>
    internal static ConsoleKey? CsiFinalKey(char final) => final switch
    {
        'A' => ConsoleKey.UpArrow,
        'B' => ConsoleKey.DownArrow,
        'C' => ConsoleKey.RightArrow,
        'D' => ConsoleKey.LeftArrow,
        'F' => ConsoleKey.End,
        'H' => ConsoleKey.Home,
        'P' => ConsoleKey.F1,
        'Q' => ConsoleKey.F2,
        'R' => ConsoleKey.F3,
        'S' => ConsoleKey.F4,
        'Z' => ConsoleKey.Tab,
        _ => null,
    };

    /// <summary>The key a <c>CSI &lt;n&gt; ~</c> sequence names, or null when it names none.</summary>
    internal static ConsoleKey? TildeKey(int code) => code switch
    {
        1 or 7 => ConsoleKey.Home,
        2 => ConsoleKey.Insert,
        3 => ConsoleKey.Delete,
        4 or 8 => ConsoleKey.End,
        5 => ConsoleKey.PageUp,
        6 => ConsoleKey.PageDown,
        11 => ConsoleKey.F1,
        12 => ConsoleKey.F2,
        13 => ConsoleKey.F3,
        14 => ConsoleKey.F4,
        15 => ConsoleKey.F5,
        17 => ConsoleKey.F6,
        18 => ConsoleKey.F7,
        19 => ConsoleKey.F8,
        20 => ConsoleKey.F9,
        21 => ConsoleKey.F10,
        23 => ConsoleKey.F11,
        24 => ConsoleKey.F12,
        _ => null,
    };

    /// <summary>A decoded key, as the press it stands for.</summary>
    private static TuiInputEvent FromNamedKey(ConsoleKey key, ConsoleModifiers modifiers) =>
        TuiInputEvent.FromKey(new ConsoleKeyInfo(
            '\0',
            key,
            modifiers.HasFlag(ConsoleModifiers.Shift),
            modifiers.HasFlag(ConsoleModifiers.Alt),
            modifiers.HasFlag(ConsoleModifiers.Control)));

    /// <summary>
    /// Reads <c>CSI &lt;params&gt; &lt;final&gt;</c>, having read the first digit.
    /// </summary>
    /// <remarks>
    /// Two finals matter here: <c>~</c> with a first parameter of 200 opens a bracketed
    /// paste, and <c>u</c> is a Kitty key report. Anything else is a sequence this reader
    /// does not claim, and is handed back as the keystrokes it arrived as.
    /// </remarks>
    private TuiInputEvent ReadParameterised(ConsoleKeyInfo escapeKey, StringBuilder seen, char first)
    {
        var parameters = new List<int>();
        var current = first - '0';

        while (true)
        {
            if (!TryTake(seen, out var next))
            {
                Replay(seen);
                return TuiInputEvent.FromKey(escapeKey);
            }

            if (char.IsAsciiDigit(next))
            {
                current = (current * 10) + (next - '0');
                continue;
            }

            if (next == ';')
            {
                parameters.Add(current);
                current = 0;
                continue;
            }

            parameters.Add(current);

            switch (next)
            {
                case '~' when parameters[0] == 200:
                    return ReadPasteBody();

                case 'u':
                    return TuiInputEvent.FromKey(TuiKittyKeyboard.ToKey(
                        parameters[0],
                        TuiKittyKeyboard.Modifiers(parameters.Count > 1 ? parameters[1] : 1)));

                default:
                    // A modified arrow is `CSI 1 ; 5 A`, and Home/End/PageUp and the
                    // function keys are `CSI <n> ~`. Both ended up as Escape here.
                    var modifiers = TuiKittyKeyboard.Modifiers(
                        parameters.Count > 1 ? parameters[1] : 1);

                    if (next == '~' && TildeKey(parameters[0]) is { } tilde)
                    {
                        return FromNamedKey(tilde, modifiers);
                    }

                    if (CsiFinalKey(next) is { } named)
                    {
                        return FromNamedKey(
                            named,
                            next == 'Z' ? modifiers | ConsoleModifiers.Shift : modifiers);
                    }

                    Replay(seen);
                    return TuiInputEvent.FromKey(escapeKey);
            }
        }
    }

    /// <summary>
    /// Reads a paste's text, the opening <c>CSI 200 ~</c> having been read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A terminal asked to bracket pastes wraps them in <c>CSI 200 ~</c> and
    /// <c>CSI 201 ~</c> — see <see cref="Rendering.TuiBracketedPaste"/>. Read whole, the
    /// newlines inside are characters rather than presses of Enter, which is the difference
    /// between pasting three lines into a field and submitting the form three times.
    /// </para>
    /// <para>
    /// An unterminated paste — the terminal was interrupted, or never sent the closing
    /// bracket — yields what arrived rather than waiting for a bracket that is not coming.
    /// </para>
    /// </remarks>
    private TuiInputEvent ReadPasteBody()
    {
        var text = new StringBuilder();
        var terminator = new StringBuilder();

        while (Console.KeyAvailable)
        {
            var next = Console.ReadKey(intercept: true).KeyChar;

            // The closing bracket is matched as it arrives, so its characters never reach the
            // text. A partial match that breaks off was text after all.
            if (next == PasteClose[terminator.Length])
            {
                terminator.Append(next);

                if (terminator.Length == PasteClose.Length)
                {
                    return TuiInputEvent.FromPaste(text.ToString());
                }

                continue;
            }

            text.Append(terminator);
            terminator.Clear();
            text.Append(next);
        }

        text.Append(terminator);
        return TuiInputEvent.FromPaste(text.ToString());
    }

    /// <summary>`ESC [ 2 0 1 ~`, the sequence that ends a paste.</summary>
    private const string PasteClose = "\x1b[201~";

    /// <summary>Reads one character, if the console has one waiting.</summary>
    private static bool TryTake(StringBuilder seen, out char character)
    {
        if (!SequenceFollows())
        {
            character = default;
            return false;
        }

        character = Console.ReadKey(intercept: true).KeyChar;
        seen.Append(character);
        return true;
    }

    /// <summary>How long to wait for the rest of an escape sequence before calling it a lone Escape.</summary>
    private const int SequenceGraceMilliseconds = 30;

    /// <summary>Whether more of an escape sequence is coming, waiting briefly to find out.</summary>
    /// <remarks>
    /// A terminal writes a sequence in one go, but the read that returned its <c>ESC</c> can
    /// still land before the remainder reaches the buffer — and asking
    /// <see cref="Console.KeyAvailable"/> at that instant answers no. The sequence was then
    /// read as a lone Escape followed by loose characters, which closes a screen that has no
    /// form to cancel. The grace costs a pressed Escape this long and nothing else.
    /// </remarks>
    private static bool SequenceFollows()
    {
        if (Console.KeyAvailable)
        {
            return true;
        }

        var deadline = Environment.TickCount64 + SequenceGraceMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (Console.KeyAvailable)
            {
                return true;
            }

            Thread.Sleep(1);
        }

        return false;
    }

    /// <summary>Hands characters back as keystrokes, in the order they were read.</summary>
    private void Replay(StringBuilder consumed)
    {
        foreach (var character in consumed.ToString())
        {
            _pendingEvents.Enqueue(TuiInputEvent.FromKey(
                new ConsoleKeyInfo(character, CharToConsoleKey(character), false, false, false)));
        }
    }

    private TuiInputEvent ReadSgrMouse(ConsoleKeyInfo escapeKey, StringBuilder seen)
    {
        var buffer = new StringBuilder();
        buffer.Append(Escape);
        buffer.Append(seen);

        // We need: [ < Pb ; Pc ; Pr M/m
        // Read character-by-character, checking for early bail.
        // Maximum reasonable length: ESC [ < nnn ; nnn ; nnn M = ~20 chars

        const int maxSequenceLength = 32;

        // `[` and `<` were read by the dispatcher that decided this was a mouse report, so
        // the position check continues from after them rather than expecting them again.
        for (var i = seen.Length; i < maxSequenceLength; i++)
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
