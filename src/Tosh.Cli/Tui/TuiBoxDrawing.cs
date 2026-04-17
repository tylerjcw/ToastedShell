using Tosh.Core;

namespace Tosh.Cli.Tui;

internal static class TuiBoxDrawing
{
    public static TuiBoxCharacters GetBoxCharacters(ToshTableBoxStyle style)
    {
        style = TerminalGlyphs.ResolveBoxStyle(style);

        return style switch
        {
            ToshTableBoxStyle.Square => new('┌', '┐', '└', '┘', '│', '─'),
            ToshTableBoxStyle.Heavy => new('┏', '┓', '┗', '┛', '┃', '━'),
            ToshTableBoxStyle.Ascii => new('+', '+', '+', '+', '|', '-'),
            ToshTableBoxStyle.Double => new('╔', '╗', '╚', '╝', '║', '═'),
            _ => new('╭', '╮', '╰', '╯', '│', '─'),
        };
    }
}

internal readonly record struct TuiBoxCharacters(
    char TopLeft,
    char TopRight,
    char BottomLeft,
    char BottomRight,
    char Vertical,
    char Horizontal);
