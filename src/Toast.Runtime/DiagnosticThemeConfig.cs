namespace Tosh.Runtime;

// `TOAST-0006`. Colour is presentation, so these belong with the shell by the classification
// this item set out — but `DiagnosticRenderer` names them, and the renderer cannot follow
// them there: it is what a *compiled program* uses to report an unhandled exception, and a
// compiled program dragging in the shell assembly is the opposite of what Phase B is for.
//
// So they travel with the language, and the wrinkle is recorded rather than hidden. Removing
// it means the renderer taking a contract instead of these classes, which is a change to the
// renderer rather than to where a file lives.

public sealed class ToshDiagnosticThemeConfig : IResettableShellConfig
{
    public ToshDiagnosticThemeConfig()
    {
        Heading = new ToshTextStyleConfig(foreground: "red", bold: true);
        Title = new ToshTextStyleConfig(foreground: "red");
        SourceLocation = new ToshTextStyleConfig(foreground: "gray", dim: true);
        Underline = new ToshTextStyleConfig(foreground: "red");
        Label = new ToshTextStyleConfig(foreground: "red");
        Help = new ToshTextStyleConfig(foreground: "bright-cyan");
        Frame = new ToshTextStyleConfig(foreground: "gray", dim: true);
        Code = new ToshTextStyleConfig(foreground: "gray", dim: true);
        ErrorGlyph = new ToshTextStyleConfig(foreground: "bright-red", bold: true);
        WarningGlyph = new ToshTextStyleConfig(foreground: "bright-yellow", bold: true);
        InfoGlyph = new ToshTextStyleConfig(foreground: "bright-blue", bold: true);
        HintGlyph = new ToshTextStyleConfig(foreground: "gray", dim: true);
    }

    public ToshTextStyleConfig Heading { get; }

    public ToshTextStyleConfig Title { get; }

    public ToshTextStyleConfig SourceLocation { get; }

    public ToshTextStyleConfig Underline { get; }

    public ToshTextStyleConfig Label { get; }

    public ToshTextStyleConfig Help { get; }

    /// <summary>Style applied to half-frame border characters (`│ ╰─`).</summary>
    public ToshTextStyleConfig Frame { get; }

    /// <summary>Style applied to the diagnostic code in the header (`tosh.runtime.unknown_command`).</summary>
    public ToshTextStyleConfig Code { get; }

    public ToshTextStyleConfig ErrorGlyph { get; }

    public ToshTextStyleConfig WarningGlyph { get; }

    public ToshTextStyleConfig InfoGlyph { get; }

    public ToshTextStyleConfig HintGlyph { get; }

    public void Reset()
    {
        Heading.Reset();
        Title.Reset();
        SourceLocation.Reset();
        Underline.Reset();
        Label.Reset();
        Help.Reset();
        Frame.Reset();
        Code.Reset();
        ErrorGlyph.Reset();
        WarningGlyph.Reset();
        InfoGlyph.Reset();
        HintGlyph.Reset();
    }
}

public sealed class ToshTextStyleConfig : IResettableShellConfig
{
    private readonly string? _defaultForeground;
    private readonly string? _defaultBackground;
    private readonly bool _defaultBold;
    private readonly bool _defaultItalic;
    private readonly bool _defaultUnderline;
    private readonly bool _defaultDim;

    public ToshTextStyleConfig(
        string? foreground = null,
        string? background = null,
        bool bold = false,
        bool italic = false,
        bool underline = false,
        bool dim = false)
    {
        _defaultForeground = foreground;
        _defaultBackground = background;
        _defaultBold = bold;
        _defaultItalic = italic;
        _defaultUnderline = underline;
        _defaultDim = dim;

        Foreground = foreground;
        Background = background;
        Bold = bold;
        Italic = italic;
        Underline = underline;
        Dim = dim;
    }

    public string? Foreground { get; set; }

    public string? Background { get; set; }

    public bool Bold { get; set; }

    public bool Italic { get; set; }

    public bool Underline { get; set; }

    public bool Dim { get; set; }

    public StyledText Apply(string text)
    {
        return new StyledText(text, Foreground, Background, Bold, Italic, Underline, Dim);
    }

    public void Reset()
    {
        Foreground = _defaultForeground;
        Background = _defaultBackground;
        Bold = _defaultBold;
        Italic = _defaultItalic;
        Underline = _defaultUnderline;
        Dim = _defaultDim;
    }
}
