using System.Drawing;

namespace Tosh.Stdlib.Plotting;

/// <summary>
/// Styling theme using standard CLR <see cref="Color"/>.
/// </summary>
public sealed class PlotTheme
{
    public Color BackgroundColor { get; init; } = Color.White;
    public Color CanvasColor { get; init; } = Color.White;
    public Color TextColor { get; init; } = Color.FromArgb(30, 30, 30);
    public Color AxisColor { get; init; } = Color.FromArgb(60, 60, 60);
    public Color GridColor { get; init; } = Color.FromArgb(230, 230, 230);
    public Color MinorGridColor { get; init; } = Color.FromArgb(245, 245, 245);
    public IReadOnlyList<Color> Palette { get; init; } = PlotColorExtensions.DefaultPalette;

    public static PlotTheme Light { get; } = new();

    public static PlotTheme Dark { get; } = new()
    {
        BackgroundColor = Color.FromArgb(26, 27, 30),
        CanvasColor = Color.FromArgb(34, 35, 39),
        TextColor = Color.FromArgb(220, 220, 220),
        AxisColor = Color.FromArgb(160, 160, 160),
        GridColor = Color.FromArgb(55, 56, 62),
        MinorGridColor = Color.FromArgb(42, 43, 48),
        Palette =
        [
            Color.FromArgb(0x4C, 0xAF, 0x50), // Green
            Color.FromArgb(0x21, 0x96, 0xF3), // Blue
            Color.FromArgb(0xFF, 0x98, 0x00), // Orange
            Color.FromArgb(0xE9, 0x1E, 0x63), // Pink
            Color.FromArgb(0x00, 0xBC, 0xD4), // Cyan
            Color.FromArgb(0x9C, 0x27, 0xB0), // Purple
            Color.FromArgb(0xFF, 0xEB, 0x3B), // Yellow
            Color.FromArgb(0xFF, 0x57, 0x22), // Deep Orange
        ]
    };
}
