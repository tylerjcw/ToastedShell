using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tosh.Tome.Settings;

/// <summary>
/// Tōme editor settings. Loaded from <c>$TOME_CONFIG</c> or
/// <c>~/.config/tome/settings.json</c>. All fields have sensible
/// defaults so a missing file is equivalent to an empty file.
/// </summary>
/// <remarks>
/// JSON keys are case-insensitive and tolerate JSONC (line/block
/// comments and trailing commas). Unknown keys are ignored so settings
/// files survive upgrades.
/// </remarks>
internal sealed class TomeSettings
{
    public StatusBarSettings StatusBar { get; init; } = new();
    public ExplorerSettings Explorer { get; init; } = new();

    // ─── Runtime-toggleable options (persisted by :set) ──────────────────

    /// <summary>When true, :w formats the buffer before writing.</summary>
    public bool FormatOnSave { get; set; } = false;

    // ─── Load / Save ──────────────────────────────────────────────────────

    /// <summary>Non-null when the settings file existed but could not be parsed.</summary>
    [JsonIgnore]
    public string? ParseWarning { get; private init; }

    public static TomeSettings Default { get; } = new();

    public static TomeSettings Load()
    {
        var path = ResolveConfigPath();
        if (path is null) return Default;
        if (!File.Exists(path)) return Default;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<TomeSettings>(json, ReadOptions) ?? Default;
        }
        catch (Exception ex)
        {
            // Settings parse failures must never crash the editor. Defaults
            // are used and the warning surfaces in the status bar on startup.
            return new TomeSettings { ParseWarning = $"settings: could not parse {path} — {ex.Message}" };
        }
    }

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions IndentedOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Writes the current settings back to the config file.
    /// Returns a non-null error string on failure, null on success.
    /// </summary>
    public string? Save()
    {
        var path = ResolveConfigPath();
        if (path is null) return "settings: cannot resolve config path ($HOME not set)";
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // Same policy as `Tosh.Runtime.ToshJson`, restated because Tome does not
            // reference that assembly. A theme name or path with a non-ASCII character
            // was written to the user's own settings file as `\uXXXX`.
            File.WriteAllText(path, JsonSerializer.Serialize(this, IndentedOptions));
            return null;
        }
        catch (Exception ex)
        {
            return $"settings: could not save — {ex.Message}";
        }
    }

    private static string? ResolveConfigPath()
    {
        var path = Environment.GetEnvironmentVariable("TOME_CONFIG");
        if (!string.IsNullOrEmpty(path)) return path;
        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrEmpty(home)) return null;
        return Path.Combine(home, ".config", "tome", "settings.json");
    }
}

/// <summary>
/// Status-bar configuration. Each <c>Show*</c> flag toggles one
/// segment; the rest customise glyphs and ordering.
/// </summary>
internal sealed class StatusBarSettings
{
    public bool ShowMode { get; init; } = true;
    public bool ShowFile { get; init; } = true;
    public bool ShowModified { get; init; } = true;
    public bool ShowLanguage { get; init; } = true;
    public bool ShowGit { get; init; } = true;
    public bool ShowDiagnostics { get; init; } = true;
    public bool ShowSelection { get; init; } = true;
    public bool ShowTabs { get; init; } = true;
    public bool ShowPosition { get; init; } = true;
    public bool ShowPercent { get; init; } = true;
    public bool ShowLineCount { get; init; } = true;

    /// <summary>Glyph rendered next to the branch name. Defaults to a plain shrug.</summary>
    public string GitGlyph { get; init; } = "\u2387"; // ⎇
    public string ModifiedGlyph { get; init; } = "\u25cf"; // ●
    public string ErrorGlyph { get; init; } = "\u2716"; // ✖
    public string WarningGlyph { get; init; } = "\u26a0"; // ⚠
    public string Separator { get; init; } = "\u2502"; // │

    /// <summary>
    /// 256-colour palette indices for the status bar. Use the names
    /// (xterm 256) when authoring settings:
    /// see <see href="https://www.ditig.com/256-colors-cheat-sheet"/>.
    /// </summary>
    public int BackgroundColor { get; init; } = 237;
    public int ForegroundColor { get; init; } = 250;
    public int SeparatorColor { get; init; } = 240;
    public int EditModeBg { get; init; } = 28;   // green
    public int EditModeFg { get; init; } = 231;  // white
    public int CommandModeBg { get; init; } = 130; // amber
    public int CommandModeFg { get; init; } = 231;
    public int GitColor { get; init; } = 215;     // peach
    public int LanguageColor { get; init; } = 110; // soft blue
    public int ModifiedColor { get; init; } = 209; // salmon
    public int ErrorColor { get; init; } = 203;    // red
    public int WarningColor { get; init; } = 215;  // amber
}

/// <summary>
/// Explorer-pane configuration: git status glyphs and colors.
/// </summary>
/// <remarks>
/// Defaults use Nerd Fonts icons (JetBrainsMono NF, Iosevka NF, etc.)
/// for status badges and the PowerlineSymbols branch glyph (U+E0A0).
/// Override individual glyphs in settings.json with plain-text fallbacks
/// (e.g. "~" / "?" / "-" / "\u2387") for non-Nerd-Font terminals.
/// </remarks>
internal sealed class ExplorerSettings
{
    /// <summary>
    /// Shown before the branch name on repo root nodes.
    /// Default: ⎇ (U+2387). Powerline alternative: "" ().
    /// </summary>
    public string BranchGlyph { get; init; } = "\ue0a0"; //  (PowerlineSymbols U+E0A0)

    /// <summary>Single-char badge for modified (staged or unstaged) files. Default: ~</summary>
    public string ChangedGlyph { get; init; } = "\uf040"; //  (nf-fa-pencil; plain fallback: ~)

    /// <summary>Single-char badge for untracked files. Default: ?</summary>
    public string UntrackedGlyph { get; init; } = "\uf055"; //  (nf-fa-plus_circle; plain fallback: ?)

    /// <summary>Single-char badge for deleted files. Default: -</summary>
    public string DeletedGlyph { get; init; } = "\uf1f8"; //  (nf-fa-trash; plain fallback: -)

    /// <summary>256-colour index for the Changed badge. Default: 215 (amber).</summary>
    public int ChangedColor { get; init; } = 215;

    /// <summary>256-colour index for the Untracked badge. Default: 114 (green).</summary>
    public int UntrackedColor { get; init; } = 114;

    /// <summary>256-colour index for the Deleted badge. Default: 203 (red).</summary>
    public int DeletedColor { get; init; } = 203;

    /// <summary>256-colour index for the branch name on repo roots. Default: 110 (soft blue).</summary>
    public int BranchColor { get; init; } = 110;
}
