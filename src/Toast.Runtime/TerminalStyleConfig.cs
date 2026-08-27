namespace Tosh.Runtime;

// `TOAST-0006`. Read by `TerminalGlyphs`, which `DiagnosticRenderer` reaches, which a
// compiled program reaches — so these follow the same reasoning as the diagnostic theme:
// presentation data the language ends up naming because error reporting must work without
// the shell. The root `ToshConfig` that composes them stays on the shell side.

public sealed class ToshTtyConfig : IResettableShellConfig
{
    private ToshTableBoxStyle _boxStyle = ToshTableBoxStyle.Square;
    private string _indicator = " > ";
    private string _errorMarker = "x";

    public bool Enabled { get; set; } = true;

    public ToshTableBoxStyle BoxStyle
    {
        get => _boxStyle;
        set => _boxStyle = value is ToshTableBoxStyle.Rounded ? ToshTableBoxStyle.Square : value;
    }

    public string Indicator
    {
        get => _indicator;
        set => _indicator = string.IsNullOrEmpty(value) ? " > " : value;
    }

    public string ErrorMarker
    {
        get => _errorMarker;
        set => _errorMarker = string.IsNullOrEmpty(value) ? "x" : value;
    }

    public ToshTtyGlyphConfig Glyphs { get; } = new();

    public void Reset()
    {
        Enabled = true;
        _boxStyle = ToshTableBoxStyle.Square;
        _indicator = " > ";
        _errorMarker = "x";
        Glyphs.Reset();
    }
}

public enum ToshTableBoxStyle
{
    Rounded,
    Square,
    Heavy,
    Ascii,
    Double,
}

public sealed class ToshTtyGlyphConfig : IResettableShellConfig, IShellRecordObject
{
    private readonly Dictionary<string, string> _glyphs = new(StringComparer.Ordinal);

    public string ShellTypeName => "TtyGlyphs";

    public ToshTtyGlyphConfig()
    {
        SetDefaults();
    }

    public string? Resolve(string glyph)
    {
        return _glyphs.TryGetValue(glyph, out var fallback) ? fallback : null;
    }

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        if (_glyphs.TryGetValue(name, out var fallback))
        {
            value = fallback;
            return true;
        }

        value = null;
        return false;
    }

    public bool TrySetMember(string name, object? value)
    {
        if (value is null)
        {
            _glyphs.Remove(name);
            return true;
        }

        _glyphs[name] = value.ToString() ?? string.Empty;
        return true;
    }

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        return _glyphs
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => new KeyValuePair<string, object?>(entry.Key, entry.Value))
            .ToArray();
    }

    public void Reset()
    {
        _glyphs.Clear();
        SetDefaults();
    }

    private void SetDefaults()
    {
        _glyphs["\u2718"] = "x";      // ✘ → x
        _glyphs["\u276f"] = ">";      // ❯ → >
        _glyphs["\ue0a0"] = "";       //  → (empty, Nerd Font)
        _glyphs["\u00d7"] = "x";      // × → x
    }
}
