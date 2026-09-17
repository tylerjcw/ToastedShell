namespace Tosh.Tui.Rendering;

/// <summary>
/// The ASCII a terminal gets when it cannot carry the characters a screen is drawn with.
/// </summary>
/// <remarks>
/// <para>
/// Every border, guide, meter and spinner in the framework is drawn out of characters above
/// U+2000. On a terminal whose encoding cannot carry them — <c>LANG=C</c>, a serial console,
/// a CI log — each one arrives as a replacement box, and a screen made of boxes is not a
/// degraded screen but an unreadable one (<c>TUI-0009</c>).
/// </para>
/// <para>
/// Only what the framework <em>draws</em> is folded. A filename with a CJK character in it
/// is the reader's data and is left exactly as it was: mangling somebody's text to protect
/// a border is the wrong way round, and a terminal that cannot show it will say so itself.
/// </para>
/// </remarks>
public static class TuiGlyphs
{
    /// <summary>
    /// What each drawn character becomes. Every replacement is one column wide, because
    /// the layout has already reserved the columns.
    /// </summary>
    private static readonly Dictionary<string, string> Ascii = Build();

    private static Dictionary<string, string> Build()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        Add(map, "-", "─━═╌╍╼╾");
        Add(map, "|", "│┃║╎╏╽╿");
        Add(map, "+", "┌┏┐┓└┗┘┛├┣┤┫┬┳┴┻┼╋╔╗╚╝╠╣╦╩╬╭╮╯╰");
        Add(map, "#", "▀▁▂▃▄▅▆▇█▌▐▖▗▘▙▚▛▜▝▞▟");
        Add(map, ":", "░▒▓");
        Add(map, ">", "→▸►▶⟩");
        Add(map, "<", "←◂◄◀⟨");
        Add(map, "^", "↑▴▲");
        Add(map, "v", "↓▾▼");
        Add(map, "*", "•◆◇○●◉◐◑◒◓◜◝◞◟◠◡✓✔✗✘★☆");
        Add(map, ".", "·…∙");
        Add(map, "x", "×✕");
        Add(map, "-", "—–");
        Add(map, "o", "ō");

        // The spinner's braille frames. Folded as a block: a turning spinner becomes a
        // steady mark rather than a column of boxes, which is the honest degradation —
        // `ascii` is the style to pick when the terminal is known to be poor.
        for (var braille = 0x2800; braille <= 0x28FF; braille += 1)
        {
            map[char.ConvertFromUtf32(braille)] = "*";
        }

        return map;

        static void Add(Dictionary<string, string> map, string ascii, string characters)
        {
            foreach (var character in characters)
            {
                map[character.ToString()] = ascii;
            }
        }
    }

    /// <summary>
    /// Whether the terminal can carry the characters a screen is drawn with.
    /// </summary>
    /// <remarks>
    /// True unless there is evidence otherwise, which is the safe polarity: a great many
    /// correct setups leave <c>LANG</c> unset, and folding those to ASCII would make a
    /// working screen worse to protect one that is not broken. What counts as evidence is a
    /// locale that names a non-UTF-8 encoding, or a terminal that says it is dumb.
    /// </remarks>
    public static bool Supported(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (environment("TOSH_TUI_ASCII") is { Length: > 0 } told)
        {
            return told.Trim() is "0" or "false" or "no";
        }

        if (environment("TERM")?.Equals("dumb", StringComparison.OrdinalIgnoreCase) == true)
        {
            return false;
        }

        // The first of these that is set decides, which is what the locale rules say.
        var locale =
            First(environment("LC_ALL")) ??
            First(environment("LC_CTYPE")) ??
            First(environment("LANG"));

        if (locale is null)
        {
            return true;
        }

        return locale.Contains("utf-8", StringComparison.OrdinalIgnoreCase) ||
               locale.Contains("utf8", StringComparison.OrdinalIgnoreCase);

        static string? First(string? value) => value is { Length: > 0 } ? value : null;
    }

    /// <summary>
    /// The ASCII stand-in for one grapheme cluster, or the cluster unchanged.
    /// </summary>
    /// <remarks>
    /// A cluster wider than one column is never folded. The layout reserved two columns for
    /// it and put a continuation cell in the second, so replacing it with one character
    /// would leave the row a column short and every border after it out of line — which is
    /// the fault this is supposed to prevent, arriving by another door.
    /// </remarks>
    public static string Fold(string cluster)
    {
        ArgumentNullException.ThrowIfNull(cluster);

        return TuiTextMeasure.ClusterWidth(cluster) == 1 &&
               Ascii.TryGetValue(cluster, out var ascii)
            ? ascii
            : cluster;
    }

    /// <summary>Every character this folds, for a test that wants to check them all.</summary>
    internal static IEnumerable<KeyValuePair<string, string>> Table => Ascii;
}
