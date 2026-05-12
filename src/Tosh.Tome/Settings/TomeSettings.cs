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

    public static TomeSettings Default { get; } = new();

    public static TomeSettings Load()
    {
        var path = Environment.GetEnvironmentVariable("TOME_CONFIG");
        if (string.IsNullOrEmpty(path))
        {
            var home = Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrEmpty(home)) return Default;
            path = Path.Combine(home, ".config", "tome", "settings.json");
        }
        if (!File.Exists(path)) return Default;

        try
        {
            var json = File.ReadAllText(path);
            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };
            return JsonSerializer.Deserialize<TomeSettings>(json, opts) ?? Default;
        }
        catch
        {
            // Settings parse failures must never crash the editor. The
            // user will see defaults; surfacing the error is a future
            // concern (`:set diag` or similar).
            return Default;
        }
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
