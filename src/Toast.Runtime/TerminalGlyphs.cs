namespace Tosh.Runtime;

/// <summary>
/// Provides terminal-capability-aware glyph selection.
/// When running on a basic terminal (e.g. Linux console where $TERM=linux),
/// returns fallback glyphs. Users can customize fallbacks via $tosh.Config.Tty.
/// </summary>
public static class TerminalGlyphs
{
    private static readonly bool s_isBasicTerminal = DetectBasicTerminal();
    private static ToshTtyConfig? s_config;

    /// <summary>True when the terminal lacks full Unicode support (e.g. bare Linux console).</summary>
    public static bool IsBasicTerminal => s_isBasicTerminal;

    /// <summary>Whether TTY fallbacks are active (detected basic terminal AND config enabled).</summary>
    public static bool IsActive => s_isBasicTerminal && (s_config?.Enabled ?? true);

    /// <summary>Binds the config so glyph resolution uses user-customizable values.</summary>
    public static void Initialize(ToshTtyConfig config)
    {
        s_config = config;
    }

    // ── Prompt glyphs ─────────────────────────────────────────────

    public static string Indicator =>
        IsActive ? (s_config?.Indicator ?? " > ") : " \u276f ";

    public static string ExitCodePrefix =>
        IsActive ? ResolveGlyph("\u2718", "x") : "\u2718";

    public static string GitBranchIcon =>
        IsActive ? ResolveGlyph("\ue0a0", "") : "\ue0a0";

    public static string ErrorMarker =>
        IsActive ? (s_config?.ErrorMarker ?? "x") : "\u00d7";

    public static string WarningMarker =>
        IsActive ? "!" : "\u26a0";

    // ── Diagnostic / source-snippet rounded corners ───────────────
    // The Linux console renders basic box-drawing (─│├┤┬┴┼┌┐└┘)
    // but NOT rounded corners (╭╮╯╰). Fall back to square equivalents.

    public static char TopLeftCorner => IsActive ? '\u250c' : '\u256d';      // ┌ instead of ╭
    public static char BottomLeftCorner => IsActive ? '\u2514' : '\u2570';   // └ instead of ╰

    // ── Table box style ───────────────────────────────────────────

    public static ToshTableBoxStyle ResolveBoxStyle(ToshTableBoxStyle configured)
    {
        if (!IsActive || configured != ToshTableBoxStyle.Rounded)
        {
            return configured;
        }

        return s_config?.BoxStyle ?? ToshTableBoxStyle.Square;
    }

    /// <summary>
    /// Resolves a glyph through the user-configurable glyph map.
    /// Returns the mapped fallback if found, otherwise the hardcoded default.
    /// </summary>
    public static string ResolveGlyph(string glyph, string hardcodedFallback)
    {
        return s_config?.Glyphs.Resolve(glyph) ?? hardcodedFallback;
    }

    private static bool DetectBasicTerminal()
    {
        var term = Environment.GetEnvironmentVariable("TERM");

        if (string.IsNullOrEmpty(term))
        {
            return false;
        }

        return term.Equals("linux", StringComparison.OrdinalIgnoreCase)
            || term.Equals("dumb", StringComparison.OrdinalIgnoreCase);
    }
}
