namespace Tosh.Tome.Theme;

/// <summary>
/// Detects what colour depth the host terminal supports. Truecolor (24-bit
/// SGR <c>38;2;r;g;b</c>) is enabled when:
/// <list type="bullet">
///   <item><c>$COLORTERM</c> is <c>truecolor</c> or <c>24bit</c> (the
///         de-facto signal advertised by xterm derivatives, kitty,
///         alacritty, wezterm, foot, ghostty, vte-based emulators, etc.)</item>
///   <item><c>$TERM</c> matches <c>*-direct</c> or <c>*-truecolor</c>
///         (ncurses' canonical terminfo capability marker)</item>
/// </list>
/// Truecolor is force-disabled when <c>$TOME_NO_TRUECOLOR</c> is set
/// (to anything non-empty). 256-color is assumed otherwise — every
/// terminal Tōme has been observed on supports it.
/// </summary>
internal static class TerminalCapabilities
{
    private static bool? _cached;

    public static bool SupportsTrueColor => _cached ??= Detect();

    /// <summary>Test-only hook; clears the memoised detection.</summary>
    internal static void ResetForTests() => _cached = null;

    private static bool Detect()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TOME_NO_TRUECOLOR")))
            return false;

        var colorterm = Environment.GetEnvironmentVariable("COLORTERM");
        if (!string.IsNullOrEmpty(colorterm))
        {
            if (colorterm.Equals("truecolor", StringComparison.OrdinalIgnoreCase)) return true;
            if (colorterm.Equals("24bit", StringComparison.OrdinalIgnoreCase)) return true;
        }

        var term = Environment.GetEnvironmentVariable("TERM");
        if (!string.IsNullOrEmpty(term))
        {
            if (term.EndsWith("-direct", StringComparison.OrdinalIgnoreCase)) return true;
            if (term.EndsWith("-truecolor", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}
