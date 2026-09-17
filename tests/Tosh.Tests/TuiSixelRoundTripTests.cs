using System.Text.RegularExpressions;
using Tosh.Tui.Rendering;

namespace Tosh.Tests;

/// <summary>
/// Encoding a picture as sixels and reading it back, which is the only way to tell a
/// well-formed one from a correct one (<c>TUI-0025</c>).
/// </summary>
/// <remarks>
/// A shape assertion says the escape sequence looks like a sixel. It cannot say the
/// picture survived, and a format whose bands are six rows counted from the wrong end
/// would pass every shape assertion ever written.
/// </remarks>
public sealed partial class TuiSixelRoundTripTests
{
    private static TuiPixels Picture(int width, int height, Func<int, int, (byte, byte, byte)> paint)
    {
        var rgb = new byte[width * height * 3];

        for (var y = 0; y < height; y += 1)
        {
            for (var x = 0; x < width; x += 1)
            {
                var (red, green, blue) = paint(x, y);
                var offset = ((y * width) + x) * 3;

                rgb[offset] = red;
                rgb[offset + 1] = green;
                rgb[offset + 2] = blue;
            }
        }

        return new TuiPixels(width, height, rgb);
    }

    /// <summary>Reads a sixel string back into pixels.</summary>
    private static (int Width, int Height, int[,] Indices) Decode(string sixel)
    {
        var body = sixel["\x1bP0;1;0q".Length..^"\x1b\\".Length];
        var raster = RasterPattern().Match(body);

        var width = int.Parse(raster.Groups[1].Value);
        var height = int.Parse(raster.Groups[2].Value);
        var pixels = new int[width, height];

        for (var y = 0; y < height; y += 1)
        {
            for (var x = 0; x < width; x += 1)
            {
                pixels[x, y] = -1;
            }
        }

        var at = raster.Index + raster.Length;
        var band = 0;
        var column = 0;
        var colour = 0;

        while (at < body.Length)
        {
            var character = body[at];

            if (character == '#')
            {
                var digits = ++at;

                while (at < body.Length && char.IsDigit(body[at])) { at += 1; }

                colour = int.Parse(body[digits..at]);

                // A definition carries its channels; a selection does not.
                if (at < body.Length && body[at] == ';')
                {
                    while (at < body.Length && body[at] != '#' && body[at] != '$' && body[at] != '-' &&
                           (char.IsDigit(body[at]) || body[at] == ';'))
                    {
                        at += 1;
                    }
                }

                continue;
            }

            if (character == '$') { column = 0; at += 1; continue; }
            if (character == '-') { band += 1; column = 0; at += 1; continue; }

            var run = 1;

            if (character == '!')
            {
                var digits = ++at;

                while (at < body.Length && char.IsDigit(body[at])) { at += 1; }

                run = int.Parse(body[digits..at]);
                character = body[at];
            }

            at += 1;

            var bits = character - '?';

            for (var repeat = 0; repeat < run; repeat += 1, column += 1)
            {
                for (var row = 0; row < 6; row += 1)
                {
                    var y = (band * 6) + row;

                    if ((bits & (1 << row)) != 0 && column < width && y < height)
                    {
                        pixels[column, y] = colour;
                    }
                }
            }
        }

        return (width, height, pixels);
    }

    [GeneratedRegex("^\"1;1;(\\d+);(\\d+)")]
    private static partial Regex RasterPattern();

    [Fact]
    public void A_picture_survives_being_written_and_read_back()
    {
        // Four quadrants, so a band counted from the wrong end or a column swept the wrong
        // way comes back visibly different rather than subtly so.
        var source = Picture(8, 8, (x, y) => (x, y) switch
        {
            ( < 4, < 4) => ((byte)255, (byte)0, (byte)0),
            ( >= 4, < 4) => ((byte)0, (byte)255, (byte)0),
            ( < 4, >= 4) => ((byte)0, (byte)0, (byte)255),
            _ => ((byte)255, (byte)255, (byte)255),
        });

        var (width, height, decoded) = Decode(TuiSixel.Encode(source));

        Assert.Equal(8, width);
        Assert.Equal(8, height);

        // Four regions, four distinct colour indices, each one uniform.
        Assert.Equal(4, new[] { decoded[0, 0], decoded[7, 0], decoded[0, 7], decoded[7, 7] }.Distinct().Count());

        for (var y = 0; y < 8; y += 1)
        {
            for (var x = 0; x < 8; x += 1)
            {
                var corner = decoded[x < 4 ? 0 : 7, y < 4 ? 0 : 7];

                Assert.Equal(corner, decoded[x, y]);
            }
        }
    }

    [Fact]
    public void A_height_that_is_not_a_multiple_of_six_keeps_its_last_rows()
    {
        // Seven rows is one full band and one row of a second, which is where an encoder
        // that assumes whole bands loses the bottom of every picture.
        var source = Picture(4, 7, (_, y) => y == 6 ? ((byte)255, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)0));

        var (_, height, decoded) = Decode(TuiSixel.Encode(source));

        Assert.Equal(7, height);
        Assert.NotEqual(decoded[0, 0], decoded[0, 6]);
    }

    [Fact]
    public void A_run_says_the_same_thing_as_the_columns_it_replaces()
    {
        // The encoder only emits a run past three, so a picture with both is the one that
        // proves the two spellings agree.
        var source = Picture(12, 6, (x, _) => x < 8 ? ((byte)255, (byte)0, (byte)0) : ((byte)(x * 20), (byte)0, (byte)0));

        var encoded = TuiSixel.Encode(source);
        var (_, _, decoded) = Decode(encoded);

        Assert.Contains("!", encoded, StringComparison.Ordinal);
        Assert.All(Enumerable.Range(0, 8), x => Assert.Equal(decoded[0, 0], decoded[x, 0]));
    }

    [Fact]
    public void Two_colours_in_one_band_are_both_kept()
    {
        // The `$` return is what makes a band hold more than one colour, and dropping it
        // would leave only whichever was written last.
        var source = Picture(2, 6, (_, y) => y < 3 ? ((byte)255, (byte)0, (byte)0) : ((byte)0, (byte)255, (byte)0));

        var encoded = TuiSixel.Encode(source);
        var (_, _, decoded) = Decode(encoded);

        Assert.Contains("$", encoded, StringComparison.Ordinal);
        Assert.NotEqual(decoded[0, 0], decoded[0, 5]);
    }
}
