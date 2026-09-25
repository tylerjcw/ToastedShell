using System.Drawing;
using System.Globalization;

namespace Tosh.Stdlib.Plotting;

/// <summary>
/// Helpers and extension methods for standard CLR <see cref="Color"/>.
/// </summary>
public static class PlotColorExtensions
{
    public static readonly Color[] DefaultPalette =
    [
        Color.FromArgb(0x1F, 0x77, 0xB4), // Muted Blue
        Color.FromArgb(0xFF, 0x7F, 0x0E), // Safety Orange
        Color.FromArgb(0x2C, 0xA0, 0x2C), // Leaf Green
        Color.FromArgb(0xD6, 0x27, 0x28), // Brick Red
        Color.FromArgb(0x94, 0x67, 0xBD), // Muted Purple
        Color.FromArgb(0x8C, 0x56, 0x4B), // Chestnut Brown
        Color.FromArgb(0xE3, 0x77, 0xC2), // Raspberry Pink
        Color.FromArgb(0x7F, 0x7F, 0x7F), // Middle Gray
        Color.FromArgb(0xBC, 0xBD, 0x22), // Olive
        Color.FromArgb(0x17, 0xBE, 0xCF)  // Teal
    ];

    public static string ToSvgHex(this Color color) =>
        color.A == 255
            ? $"#{color.R:x2}{color.G:x2}{color.B:x2}"
            : $"rgba({color.R},{color.G},{color.B},{(color.A / 255.0):F2})";

    public static string ToAnsi(this Color color) =>
        $"\x1b[38;2;{color.R};{color.G};{color.B}m";

    public static string ToAnsiBackground(this Color color) =>
        $"\x1b[48;2;{color.R};{color.G};{color.B}m";

    public static Color ParseColor(string? input, Color fallback = default)
    {
        if (string.IsNullOrWhiteSpace(input)) return fallback == default ? Color.Black : fallback;
        input = input.Trim();

        if (input.StartsWith("#"))
        {
            var hex = input[1..];
            if (hex.Length == 3)
            {
                var r = (byte)(byte.Parse(hex[0..1], NumberStyles.HexNumber, CultureInfo.InvariantCulture) * 17);
                var g = (byte)(byte.Parse(hex[1..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture) * 17);
                var b = (byte)(byte.Parse(hex[2..3], NumberStyles.HexNumber, CultureInfo.InvariantCulture) * 17);
                return Color.FromArgb(r, g, b);
            }
            if (hex.Length == 6)
            {
                var r = byte.Parse(hex[0..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var g = byte.Parse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var b = byte.Parse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return Color.FromArgb(r, g, b);
            }
            if (hex.Length == 8)
            {
                var a = byte.Parse(hex[0..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var r = byte.Parse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var g = byte.Parse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var b = byte.Parse(hex[6..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return Color.FromArgb(a, r, g, b);
            }
        }

        var named = Color.FromName(input);
        if (named.IsKnownColor) return named;

        return input.ToLowerInvariant() switch
        {
            "r" => Color.Red,
            "g" => Color.Green,
            "b" => Color.Blue,
            "c" => Color.Cyan,
            "m" => Color.Magenta,
            "y" => Color.Yellow,
            "k" => Color.Black,
            "w" => Color.White,
            _ => fallback == default ? Color.Black : fallback
        };
    }
}
