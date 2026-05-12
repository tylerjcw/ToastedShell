using System.Text;

namespace Tosh.Tome.Theme;

/// <summary>
/// Named styling roles consumed by colorizers and editor chrome. Each
/// role resolves to an SGR escape string via <see cref="TomeTheme"/>.
/// Adding a role: add it here, then map it in <see cref="TomeTheme.BuiltinDark"/>.
/// </summary>
internal enum Role
{
    // ─── Syntax roles ──────────────────────────────────────────────
    Keyword,
    ControlFlow,
    String,
    EscapedString,
    Interpolated,
    Number,
    Constant,
    Operator,
    Punctuation,
    Variable,
    Flag,
    Comment,
    DocComment,        // composes with Comment via italics
    TypeName,
    FunctionName,
    Heading,           // markdown
    Emphasis,
    Strong,

    // ─── Chrome roles ──────────────────────────────────────────────
    CurrentLineBg,
    GutterCurrentLine,
    GutterDiagError,
    GutterDiagWarn,
    GutterDiagInfo,
    GutterDim,         // faint depth bars

    StatusBarBg,       // explorer banner, status line accents
    PopupBg,           // completion popup default-row bg
    PopupSelectedBg,   // completion popup selected-row bg (uses reverse video)

    ExplorerSelectedFocused,
    ExplorerSelectedUnfocused,
}

/// <summary>
/// An RGB triple plus a 256-color fallback index. <see cref="TomeTheme"/>
/// emits the truecolor or the indexed form depending on
/// <see cref="TerminalCapabilities.SupportsTrueColor"/>.
/// </summary>
internal readonly record struct Color(byte R, byte G, byte B, byte Indexed)
{
    /// <summary>SGR foreground sequence for this color.</summary>
    public string Fg(bool truecolor) => truecolor
        ? $"\u001b[38;2;{R};{G};{B}m"
        : $"\u001b[38;5;{Indexed}m";

    /// <summary>SGR background sequence for this color.</summary>
    public string Bg(bool truecolor) => truecolor
        ? $"\u001b[48;2;{R};{G};{B}m"
        : $"\u001b[48;5;{Indexed}m";
}

/// <summary>
/// A precomputed SGR-open sequence keyed by <see cref="Role"/>.
/// Themes precompute every role at construction so the hot render path
/// is a plain dictionary lookup with no string formatting.
/// </summary>
internal sealed class TomeTheme
{
    private readonly Dictionary<Role, string> _open;

    public string Reset { get; } = "\u001b[0m";
    public bool TrueColor { get; }
    public string Name { get; }

    private TomeTheme(string name, bool truecolor, Dictionary<Role, string> open)
    {
        Name = name;
        TrueColor = truecolor;
        _open = open;
    }

    /// <summary>SGR-open string for <paramref name="role"/>. Always defined.</summary>
    public string Open(Role role) => _open[role];

    /// <summary>Convenience: "<see cref="Open"/>(role) + text + <see cref="Reset"/>".</summary>
    public string Wrap(Role role, string text) => Open(role) + text + Reset;

    /// <summary>
    /// Process-wide active theme, lazily initialised from
    /// <see cref="TerminalCapabilities.SupportsTrueColor"/>. Replaceable
    /// for tests via <see cref="OverrideForTests"/>.
    /// </summary>
    public static TomeTheme Active => _active ??= BuiltinDark(TerminalCapabilities.SupportsTrueColor);
    private static TomeTheme? _active;

    /// <summary>Test-only hook for swapping the active theme.</summary>
    internal static void OverrideForTests(TomeTheme? theme) => _active = theme;

    /// <summary>
    /// Default dark theme. Colour values are the truecolor renderings of
    /// the original hard-coded 256-color palette so visuals don't regress
    /// on indexed terminals.
    /// </summary>
    public static TomeTheme BuiltinDark(bool truecolor)
    {
        // Colour palette (RGB picked to approximate the legacy xterm-256
        // indices listed in the trailing comment, then anchored to the
        // canonical hue).
        var keyword       = new Color(0xAF, 0x87, 0xFF, 141); // soft purple
        var controlFlow   = new Color(0xFF, 0x5F, 0x87, 204); // pink/red
        var stringG       = new Color(0xAF, 0xD7, 0x87, 150); // green
        var escapedString = new Color(0x87, 0xAF, 0x87, 108); // muted green
        var interpolated  = new Color(0xAF, 0xAF, 0x87, 144); // tan-green
        var number        = new Color(0xFF, 0xAF, 0x5F, 215); // orange
        var soft          = new Color(0x87, 0xAF, 0xD7, 110); // soft blue
        var tan           = new Color(0xD7, 0xAF, 0x87, 180); // tan
        var grey245       = new Color(0x8A, 0x8A, 0x8A, 245); // grey
        var grey244       = new Color(0x80, 0x80, 0x80, 244); // dim grey
        var red203        = new Color(0xFF, 0x5F, 0x5F, 203); // bright red
        var yellow221     = new Color(0xFF, 0xD7, 0x5F, 221); // bright yellow
        var bg236         = new Color(0x30, 0x30, 0x30, 236); // current-line / popup bg
        var bg238         = new Color(0x44, 0x44, 0x44, 238); // explorer unfocused

        const string Bold = "\u001b[1m";
        const string Italic = "\u001b[3m";
        const string Faint = "\u001b[2m";

        var open = new Dictionary<Role, string>
        {
            // syntax
            [Role.Keyword]        = keyword.Fg(truecolor),
            [Role.ControlFlow]    = controlFlow.Fg(truecolor),
            [Role.String]         = stringG.Fg(truecolor),
            [Role.EscapedString]  = escapedString.Fg(truecolor),
            [Role.Interpolated]   = interpolated.Fg(truecolor),
            [Role.Number]         = number.Fg(truecolor),
            [Role.Constant]       = number.Fg(truecolor),
            [Role.Operator]       = soft.Fg(truecolor),
            [Role.Punctuation]    = grey245.Fg(truecolor),
            [Role.Variable]       = soft.Fg(truecolor),
            [Role.Flag]           = tan.Fg(truecolor),
            [Role.Comment]        = grey244.Fg(truecolor),
            [Role.DocComment]     = Italic + grey244.Fg(truecolor),
            [Role.TypeName]       = tan.Fg(truecolor),
            [Role.FunctionName]   = tan.Fg(truecolor),
            [Role.Heading]        = Bold + keyword.Fg(truecolor),
            [Role.Emphasis]       = Italic,
            [Role.Strong]         = Bold,

            // chrome
            [Role.CurrentLineBg]      = bg236.Bg(truecolor),
            [Role.GutterCurrentLine]  = number.Fg(truecolor),
            [Role.GutterDiagError]    = red203.Fg(truecolor) + Bold,
            [Role.GutterDiagWarn]     = yellow221.Fg(truecolor) + Bold,
            [Role.GutterDiagInfo]     = soft.Fg(truecolor),
            [Role.GutterDim]          = Faint,

            [Role.StatusBarBg]        = Bold + bg236.Bg(truecolor),
            [Role.PopupBg]            = bg236.Bg(truecolor),
            [Role.PopupSelectedBg]    = "\u001b[7m", // reverse video — preserved verbatim

            [Role.ExplorerSelectedFocused]   = "\u001b[7m",
            [Role.ExplorerSelectedUnfocused] = bg238.Bg(truecolor),
        };

        return new TomeTheme(truecolor ? "dark+truecolor" : "dark+256", truecolor, open);
    }
}
