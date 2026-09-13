using Tosh.Runtime;

namespace Tosh.Tui.Rendering;

/// <summary>Reads a themed style as the widget layer draws it.</summary>
/// <remarks>
/// The shell's theme is a <see cref="ToshTextStyleConfig"/> — six settable properties that
/// know how to wrap a string in escape codes. A widget does not wrap strings; it fills
/// cells, and a cell carries a <see cref="TuiStyle"/>. This is the one place that turns one
/// into the other, so the browsers can go on theming themselves out of the user's config
/// while drawing through widgets.
/// </remarks>
public static class TuiThemeStyles
{
    /// <summary>The widget-layer style a themed one describes.</summary>
    public static TuiStyle ToStyle(this ToshTextStyleConfig? style)
    {
        if (style is null)
        {
            return TuiStyle.Default;
        }

        var attributes = TuiTextAttributes.None;

        if (style.Bold)
        {
            attributes |= TuiTextAttributes.Bold;
        }

        if (style.Dim)
        {
            attributes |= TuiTextAttributes.Dim;
        }

        if (style.Italic)
        {
            attributes |= TuiTextAttributes.Italic;
        }

        if (style.Underline)
        {
            attributes |= TuiTextAttributes.Underline;
        }

        return new TuiStyle(style.Foreground, style.Background, attributes);
    }
}
