using System.Text;

namespace Tosh.Tui.Rendering;

/// <summary>
/// Encodes a picture as sixels, for the terminals that speak that and not Kitty's protocol.
/// </summary>
/// <remarks>
/// <para>
/// A sixel is six vertical pixels in one character, which is where the name comes from. An
/// image is written band by band, six rows at a time, and within a band once per colour:
/// pick a colour, sweep left to right emitting which of the six rows that column has it in,
/// carriage-return, pick the next colour, sweep again.
/// </para>
/// <para>
/// It is painted into the screen rather than placed on it. That is the one thing that makes
/// it different from Kitty's protocol and it shapes everything around it: there is no id,
/// nothing to delete, and a picture that goes away is removed by repainting the cells it
/// covered — which is the terminal writer's job, not this one's (<c>TUI-0025</c>).
/// </para>
/// </remarks>
public static class TuiSixel
{
    /// <summary>
    /// How many levels each channel is reduced to, giving a palette of its cube.
    /// </summary>
    /// <remarks>
    /// Six is 216 colours, which fits inside the 256 registers a terminal is obliged to
    /// offer and is the same cube the 256-colour palette uses — so a terminal showing this
    /// beside ordinary coloured text is showing two things from one family. Median-cut
    /// would be better and is a great deal more code for a preview pane.
    /// </remarks>
    private const int Levels = 6;

    /// <summary>The sixel character for a column of six bits.</summary>
    /// <remarks>Six bits, bottom row highest, offset into printable ASCII.</remarks>
    private static char Glyph(int bits) => (char)('?' + bits);

    /// <summary>Writes a picture as one sixel string.</summary>
    public static string Encode(TuiPixels pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);

        if (pixels.IsEmpty)
        {
            return string.Empty;
        }

        var indexed = Quantise(pixels, out var used);
        var builder = new StringBuilder();

        // `0;1;0` is the usual introducer: pixel aspect 1:1, background left alone.
        builder.Append("\x1bP0;1;0q");

        // Raster attributes, so a terminal knows how much room to leave before it starts.
        builder.Append($"\"1;1;{pixels.Width};{pixels.Height}");

        foreach (var colour in used)
        {
            // Sixel colours are percentages, not bytes, which is the one place this format
            // will quietly draw the wrong thing if you forget.
            var (red, green, blue) = Channels(colour);

            builder.Append($"#{colour};2;{red * 100 / (Levels - 1)};{green * 100 / (Levels - 1)};{blue * 100 / (Levels - 1)}");
        }

        for (var top = 0; top < pixels.Height; top += 6)
        {
            var written = false;

            foreach (var colour in used)
            {
                if (!Band(builder, indexed, pixels.Width, pixels.Height, top, colour, written))
                {
                    continue;
                }

                written = true;
            }

            if (top + 6 < pixels.Height)
            {
                builder.Append('-');
            }
        }

        builder.Append("\x1b\\");

        return builder.ToString();
    }

    /// <summary>
    /// Writes one colour's contribution to one band, or answers false if it has none.
    /// </summary>
    private static bool Band(
        StringBuilder builder,
        byte[] indexed,
        int width,
        int height,
        int top,
        int colour,
        bool alreadyWritten)
    {
        Span<int> columns = width <= 512 ? stackalloc int[width] : new int[width];
        var any = false;

        for (var x = 0; x < width; x += 1)
        {
            var bits = 0;

            for (var row = 0; row < 6 && top + row < height; row += 1)
            {
                if (indexed[((top + row) * width) + x] == colour)
                {
                    bits |= 1 << row;
                }
            }

            columns[x] = bits;
            any |= bits != 0;
        }

        if (!any)
        {
            return false;
        }

        // A carriage return rather than a newline: the next colour paints over the same
        // band, which is how one band ends up with more than one colour in it.
        if (alreadyWritten)
        {
            builder.Append('$');
        }

        builder.Append('#').Append(colour);

        // Run-length encoded, because a preview is mostly runs and the difference between
        // sending them and not is a factor of ten on a photograph.
        var run = 1;

        for (var x = 1; x <= width; x += 1)
        {
            if (x < width && columns[x] == columns[x - 1])
            {
                run += 1;
                continue;
            }

            Append(builder, Glyph(columns[x - 1]), run);
            run = 1;
        }

        return true;
    }

    private static void Append(StringBuilder builder, char glyph, int run)
    {
        // Three characters to say "three of these" is no saving, so short runs go as they
        // are. Four is where `!4X` starts to be shorter than `XXXX`.
        if (run < 4)
        {
            builder.Append(glyph, run);
            return;
        }

        builder.Append('!').Append(run).Append(glyph);
    }

    /// <summary>Reduces every pixel to the colour cube, and says which of them were used.</summary>
    private static byte[] Quantise(TuiPixels pixels, out IReadOnlyList<int> used)
    {
        var indexed = new byte[pixels.Width * pixels.Height];
        var seen = new bool[Levels * Levels * Levels];

        for (var y = 0; y < pixels.Height; y += 1)
        {
            for (var x = 0; x < pixels.Width; x += 1)
            {
                var (red, green, blue) = pixels[x, y];
                var index = (Level(red) * Levels * Levels) + (Level(green) * Levels) + Level(blue);

                indexed[(y * pixels.Width) + x] = (byte)index;
                seen[index] = true;
            }
        }

        var palette = new List<int>();

        for (var index = 0; index < seen.Length; index += 1)
        {
            if (seen[index])
            {
                palette.Add(index);
            }
        }

        used = palette;
        return indexed;
    }

    private static int Level(byte channel) => channel * (Levels - 1) / 255;

    private static (int Red, int Green, int Blue) Channels(int index)
        => (index / (Levels * Levels), index / Levels % Levels, index % Levels);
}
