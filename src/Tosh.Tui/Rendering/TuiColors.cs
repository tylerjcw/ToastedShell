using System.Globalization;
using System.Text;

namespace Tosh.Tui.Rendering;

/// <summary>What a terminal can show, from every colour to none at all.</summary>
public enum TuiColorDepth
{
    /// <summary>No colour. Attributes still work, and carry what they can.</summary>
    None,

    /// <summary>The sixteen colours every terminal has had since the 1980s.</summary>
    Ansi16,

    /// <summary>The xterm 256-colour palette: a 6×6×6 cube and a grey ramp.</summary>
    Ansi256,

    /// <summary>24-bit colour, where a hex value is sent as itself.</summary>
    TrueColor,
}

/// <summary>
/// Turns a <see cref="TuiStyle"/> into an SGR introducer, at whatever depth the terminal
/// has.
/// </summary>
/// <remarks>
/// <para>
/// The TUI emitted <c>38;2;r;g;b</c> at every reader regardless of what they were reading
/// it on (<c>TUI-0009</c>). A terminal that does not understand truecolor does not ignore
/// those bytes — it prints some of them — so the failure is not a screen in the wrong
/// colours but a screen with numbers scattered through it.
/// </para>
/// <para>
/// Degrading rather than dropping is the point. A selected row drawn with a background is
/// still the selected row on a monochrome terminal, so a background that cannot be shown
/// becomes reverse video; only then is the colour itself lost.
/// </para>
/// </remarks>
public static class TuiColors
{
    /// <summary>
    /// The sixteen ANSI colours as xterm renders them, for finding the nearest one.
    /// </summary>
    /// <remarks>
    /// xterm's values rather than the "pure" ones a reader might expect — its red is
    /// <c>205,0,0</c> and not <c>255,0,0</c>. Matching against pure values sends every
    /// mid-toned colour to the bright half of the palette, which is how a dimmed theme
    /// comes out shouting.
    /// </remarks>
    private static readonly (string Name, int R, int G, int B)[] Ansi =
    [
        ("black", 0, 0, 0),
        ("red", 205, 0, 0),
        ("green", 0, 205, 0),
        ("yellow", 205, 205, 0),
        ("blue", 0, 0, 238),
        ("magenta", 205, 0, 205),
        ("cyan", 0, 205, 205),
        ("white", 229, 229, 229),
        ("gray", 127, 127, 127),
        ("bright-red", 255, 0, 0),
        ("bright-green", 0, 255, 0),
        ("bright-yellow", 255, 255, 0),
        ("bright-blue", 92, 92, 255),
        ("bright-magenta", 255, 0, 255),
        ("bright-cyan", 0, 255, 255),
        ("bright-white", 255, 255, 255),
    ];

    /// <summary>The six levels a channel takes in the 256-colour cube.</summary>
    private static readonly int[] CubeLevels = [0, 95, 135, 175, 215, 255];

    /// <summary>Foreground SGR codes for the names a theme may use.</summary>
    private static readonly Dictionary<string, int> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = 30,
        ["red"] = 31,
        ["green"] = 32,
        ["yellow"] = 33,
        ["blue"] = 34,
        ["magenta"] = 35,
        ["cyan"] = 36,
        ["white"] = 37,
        ["gray"] = 90,
        ["grey"] = 90,
        ["brightblack"] = 90,
        ["bright-black"] = 90,
        ["brightred"] = 91,
        ["bright-red"] = 91,
        ["brightgreen"] = 92,
        ["bright-green"] = 92,
        ["brightyellow"] = 93,
        ["bright-yellow"] = 93,
        ["brightblue"] = 94,
        ["bright-blue"] = 94,
        ["brightmagenta"] = 95,
        ["bright-magenta"] = 95,
        ["brightcyan"] = 96,
        ["bright-cyan"] = 96,
        ["brightwhite"] = 97,
        ["bright-white"] = 97,
    };

    /// <summary>
    /// What the terminal this process is attached to can show.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read from the environment for the same reason <see cref="TuiGraphics.Detect"/> is:
    /// the terminal can be asked, but its answer arrives in the same stream as the
    /// keyboard, so asking means racing the reader.
    /// </para>
    /// <para>
    /// <c>NO_COLOR</c> wins over everything that is not an explicit override. The
    /// convention is that any non-empty value means no colour — not the string
    /// <c>"1"</c> — because the point of it is that a reader sets it once and every
    /// program obeys without being told how to spell yes.
    /// </para>
    /// </remarks>
    public static TuiColorDepth Detect(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (Depth(environment("TOSH_TUI_COLOR")) is { } told)
        {
            return told;
        }

        if (environment("NO_COLOR") is { Length: > 0 })
        {
            return TuiColorDepth.None;
        }

        // A terminal that says it is dumb is telling the truth about itself, and is the
        // one case where even the sixteen colours are not safe.
        var term = environment("TERM") ?? string.Empty;

        if (term.Length == 0 || term.Equals("dumb", StringComparison.OrdinalIgnoreCase))
        {
            return Forced(environment) ?? TuiColorDepth.None;
        }

        if (Forced(environment) is { } forced)
        {
            return forced;
        }

        // COLORTERM is what a truecolor terminal sets about itself, and the only reliable
        // signal: TERM is usually xterm-256color whatever the terminal can really do.
        var colorTerm = environment("COLORTERM") ?? string.Empty;

        if (colorTerm.Contains("truecolor", StringComparison.OrdinalIgnoreCase) ||
            colorTerm.Contains("24bit", StringComparison.OrdinalIgnoreCase))
        {
            return TuiColorDepth.TrueColor;
        }

        return term.Contains("256color", StringComparison.OrdinalIgnoreCase)
            ? TuiColorDepth.Ansi256
            : TuiColorDepth.Ansi16;
    }

    /// <summary>What <c>FORCE_COLOR</c> asks for, if it asks for anything.</summary>
    /// <remarks>
    /// The other half of <c>NO_COLOR</c>, for the case a reader is piping into something
    /// that does understand escapes. The levels follow the convention the JavaScript
    /// ecosystem settled on, because that is where readers will have met it.
    /// </remarks>
    private static TuiColorDepth? Forced(Func<string, string?> environment)
        => (environment("FORCE_COLOR")?.Trim() ?? string.Empty) switch
        {
            "" => null,
            "0" => TuiColorDepth.None,
            "1" => TuiColorDepth.Ansi16,
            "2" => TuiColorDepth.Ansi256,
            _ => TuiColorDepth.TrueColor,
        };

    /// <summary>A depth a reader named by hand, or null if they named nothing useful.</summary>
    private static TuiColorDepth? Depth(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "truecolor" or "24bit" or "full" => TuiColorDepth.TrueColor,
            "256" or "8bit" => TuiColorDepth.Ansi256,
            "16" or "ansi" or "basic" => TuiColorDepth.Ansi16,
            "none" or "off" or "mono" => TuiColorDepth.None,
            _ => null,
        };

    /// <summary>The escape that puts a terminal into <paramref name="style"/>.</summary>
    /// <returns>An empty string for the terminal's own default styling.</returns>
    public static string Introducer(TuiStyle style, TuiColorDepth depth)
    {
        var attributes = style.Attributes;

        // A background that cannot be drawn becomes reverse video, so whatever it was
        // distinguishing — a selected row, a highlighted match — stays distinguished. Only
        // done when the background is genuinely unshowable, and not when it is already
        // reversed, which would turn the highlight back off again.
        if (depth == TuiColorDepth.None &&
            style.Background is not null &&
            !attributes.HasFlag(TuiTextAttributes.Reverse))
        {
            attributes |= TuiTextAttributes.Reverse;
        }

        var codes = new List<string>();

        if (attributes.HasFlag(TuiTextAttributes.Bold)) { codes.Add("1"); }
        if (attributes.HasFlag(TuiTextAttributes.Dim)) { codes.Add("2"); }
        if (attributes.HasFlag(TuiTextAttributes.Italic)) { codes.Add("3"); }
        if (attributes.HasFlag(TuiTextAttributes.Underline)) { codes.Add("4"); }
        if (attributes.HasFlag(TuiTextAttributes.Reverse)) { codes.Add("7"); }

        if (depth != TuiColorDepth.None)
        {
            Add(codes, style.Foreground, depth, background: false);
            Add(codes, style.Background, depth, background: true);
        }

        return codes.Count == 0 ? string.Empty : $"\x1b[{string.Join(';', codes)}m";
    }

    private static void Add(List<string> codes, string? color, TuiColorDepth depth, bool background)
    {
        if (color is null)
        {
            return;
        }

        // A named colour is already one of the sixteen, so there is nothing to reduce: it
        // is drawable at every depth that has colour at all.
        if (Named.TryGetValue(color, out var code))
        {
            codes.Add((background ? code + 10 : code).ToString(CultureInfo.InvariantCulture));
            return;
        }

        if (!TryParseHex(color, out var r, out var g, out var b))
        {
            return;
        }

        var lead = background ? 48 : 38;

        switch (depth)
        {
            case TuiColorDepth.TrueColor:
                codes.Add($"{lead};2;{r};{g};{b}");
                break;

            case TuiColorDepth.Ansi256:
                codes.Add($"{lead};5;{Nearest256(r, g, b)}");
                break;

            default:
                var nearest = Named[Ansi[Nearest16(r, g, b)].Name];
                codes.Add((background ? nearest + 10 : nearest).ToString(CultureInfo.InvariantCulture));
                break;
        }
    }

    /// <summary>The index in the xterm 256-colour palette closest to a colour.</summary>
    /// <remarks>
    /// Both halves of the palette are tried. The cube's channels step unevenly and its
    /// greys are coarse, so a near-grey like <c>#3a3a3a</c> lands much closer on the
    /// twenty-four step ramp than on the cube — and a dim theme is mostly near-greys.
    /// </remarks>
    public static int Nearest256(int r, int g, int b)
    {
        var cube = (Level(r) * 36) + (Level(g) * 6) + Level(b) + 16;

        var cubeDistance = Distance(
            r, g, b,
            CubeLevels[Level(r)], CubeLevels[Level(g)], CubeLevels[Level(b)]);

        // The ramp runs 8, 18, 28 … 238, which is 232 + n for n in 0..23.
        var step = Math.Clamp((int)Math.Round((((r + g + b) / 3.0) - 8) / 10.0), 0, 23);
        var greyValue = 8 + (step * 10);
        var greyDistance = Distance(r, g, b, greyValue, greyValue, greyValue);

        return greyDistance < cubeDistance ? 232 + step : cube;

        static int Level(int channel)
        {
            var best = 0;

            for (var index = 1; index < CubeLevels.Length; index += 1)
            {
                if (Math.Abs(CubeLevels[index] - channel) < Math.Abs(CubeLevels[best] - channel))
                {
                    best = index;
                }
            }

            return best;
        }
    }

    /// <summary>The index in <see cref="Ansi"/> closest to a colour.</summary>
    public static int Nearest16(int r, int g, int b)
    {
        var best = 0;
        var bestDistance = int.MaxValue;

        for (var index = 0; index < Ansi.Length; index += 1)
        {
            var (_, cr, cg, cb) = Ansi[index];
            var distance = Distance(r, g, b, cr, cg, cb);

            if (distance < bestDistance)
            {
                best = index;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>Squared distance in RGB, which is enough to rank candidates.</summary>
    private static int Distance(int r, int g, int b, int cr, int cg, int cb)
        => ((r - cr) * (r - cr)) + ((g - cg) * (g - cg)) + ((b - cb) * (b - cb));

    /// <summary>Reads <c>#rgb</c> or <c>#rrggbb</c>.</summary>
    private static bool TryParseHex(string value, out int r, out int g, out int b)
    {
        r = g = b = 0;

        var text = value.AsSpan().Trim();

        if (text.Length > 0 && text[0] == '#')
        {
            text = text[1..];
        }

        if (text.Length == 3)
        {
            // #abc is #aabbcc: each digit stands for both nibbles.
            return Nibble(text[0], out r) && Nibble(text[1], out g) && Nibble(text[2], out b);
        }

        return text.Length == 6 &&
               Byte(text[..2], out r) &&
               Byte(text[2..4], out g) &&
               Byte(text[4..], out b);

        static bool Nibble(char digit, out int value)
        {
            if (!Byte(new ReadOnlySpan<char>([digit, digit]), out value))
            {
                return false;
            }

            return true;
        }

        static bool Byte(ReadOnlySpan<char> pair, out int value)
            => int.TryParse(pair, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }
}
