namespace Tosh.Tui.Rendering;

/// <summary>Text attributes a terminal can apply independently of colour.</summary>
[Flags]
public enum TuiTextAttributes
{
    None = 0,
    Bold = 1 << 0,
    Dim = 1 << 1,
    Italic = 1 << 2,
    Underline = 1 << 3,
    Reverse = 1 << 4,
}

/// <summary>How one cell is drawn.</summary>
/// <remarks>
/// Colours stay as the strings the theme already uses rather than being parsed here.
/// Parsing belongs with the terminal writer, which is also where a colour has to be
/// degraded to what the terminal can actually show — see <c>TUI-0009</c>. Introducing a
/// colour type now would mean introducing it twice.
/// </remarks>
public readonly record struct TuiStyle(
    string? Foreground = null,
    string? Background = null,
    TuiTextAttributes Attributes = TuiTextAttributes.None)
{
    /// <summary>The terminal's own colours, with no attributes.</summary>
    public static readonly TuiStyle Default = new();

    public bool IsDefault =>
        Foreground is null && Background is null && Attributes == TuiTextAttributes.None;
}
