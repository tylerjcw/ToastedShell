using System.Text;

namespace Tosh.Tui.Rendering;

/// <summary>
/// A rectangle of colour, before anything has decided how to show it on a terminal.
/// </summary>
/// <remarks>
/// <para>
/// The widget knows pixels and nothing else. Decoding a PNG, pulling a frame out of a
/// video, rasterising a page of a PDF — those are all somebody producing pixels, and
/// keeping them outside means the TUI has no image dependency and every source is
/// pluggable the same way (<c>TUI-0025</c>).
/// </para>
/// <para>
/// Three bytes a pixel, row-major. Alpha is deliberately absent: a terminal cell has no
/// transparency to composite against, so a source that has one has to decide what to put
/// behind it before it gets here.
/// </para>
/// </remarks>
public sealed class TuiPixels
{
    private readonly byte[] _rgb;

    public TuiPixels(int width, int height, byte[] rgb)
    {
        ArgumentNullException.ThrowIfNull(rgb);
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);

        if (rgb.Length < width * height * 3)
        {
            throw new ArgumentException(
                $"A {width}x{height} image needs {width * height * 3} bytes, not {rgb.Length}.",
                nameof(rgb));
        }

        Width = width;
        Height = height;
        _rgb = rgb;
    }

    public int Width { get; }

    public int Height { get; }

    public bool IsEmpty => Width == 0 || Height == 0;

    /// <summary>One pixel, clamped to the edge rather than throwing.</summary>
    /// <remarks>
    /// Clamped because every caller is sampling — scaling, cropping, letterboxing — and a
    /// sampler that has to bounds-check every read is a sampler with the same three lines
    /// in it five times.
    /// </remarks>
    public (byte Red, byte Green, byte Blue) this[int x, int y]
    {
        get
        {
            if (IsEmpty)
            {
                return (0, 0, 0);
            }

            var offset = ((Math.Clamp(y, 0, Height - 1) * Width) + Math.Clamp(x, 0, Width - 1)) * 3;

            return (_rgb[offset], _rgb[offset + 1], _rgb[offset + 2]);
        }
    }

    /// <summary>That pixel as the hex a style carries.</summary>
    public string HexAt(int x, int y)
    {
        var (red, green, blue) = this[x, y];

        return $"#{red:x2}{green:x2}{blue:x2}";
    }

    /// <summary>
    /// The same picture at a different size, by nearest neighbour.
    /// </summary>
    /// <remarks>
    /// Nearest neighbour rather than anything better because the result is about to be
    /// scaled again by the terminal into a box of cells, and the only job here is to stop
    /// four megabytes going down a pipe to be thrown away at the other end.
    /// </remarks>
    public TuiPixels Resampled(int width, int height)
    {
        if (IsEmpty || width <= 0 || height <= 0)
        {
            return this;
        }

        if (width == Width && height == Height)
        {
            return this;
        }

        var rgb = new byte[width * height * 3];

        for (var y = 0; y < height; y += 1)
        {
            for (var x = 0; x < width; x += 1)
            {
                var (red, green, blue) = this[x * Width / width, y * Height / height];
                var offset = ((y * width) + x) * 3;

                rgb[offset] = red;
                rgb[offset + 1] = green;
                rgb[offset + 2] = blue;
            }
        }

        return new TuiPixels(width, height, rgb);
    }

    /// <summary>
    /// Reads binary PPM — the format every image tool can already write.
    /// </summary>
    /// <remarks>
    /// <c>P6</c>, which is a five-token header and then the bytes. Chosen so that
    /// <c>magick</c>, <c>ffmpeg</c> and <c>pdftoppm</c> are all sources without anything
    /// here having to know what a PNG is: the parser is forty lines and the alternative is
    /// an image library in a terminal toolkit.
    /// </remarks>
    public static TuiPixels? FromPpm(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

        if (ReadToken(reader) != "P6" ||
            !int.TryParse(ReadToken(reader), out var width) ||
            !int.TryParse(ReadToken(reader), out var height) ||
            !int.TryParse(ReadToken(reader), out var maximum) ||
            width <= 0 || height <= 0 || maximum is not (> 0 and <= 65535))
        {
            return null;
        }

        // Sixteen bits a sample is legal PPM and is what ImageMagick writes by default, so
        // it is read rather than refused. A terminal has eight, so the high byte is the
        // whole of the answer and the low one is dropped.
        var wide = maximum > 255;
        var expected = width * height * 3 * (wide ? 2 : 1);
        var read = reader.ReadBytes(expected);

        if (read.Length != expected)
        {
            return null;
        }

        if (!wide)
        {
            return new TuiPixels(width, height, read);
        }

        var rgb = new byte[width * height * 3];

        for (var index = 0; index < rgb.Length; index += 1)
        {
            rgb[index] = read[index * 2];
        }

        return new TuiPixels(width, height, rgb);
    }

    /// <summary>One whitespace-separated header token, skipping comments.</summary>
    private static string ReadToken(BinaryReader reader)
    {
        var token = new StringBuilder();

        while (true)
        {
            int next;

            try
            {
                next = reader.ReadByte();
            }
            catch (EndOfStreamException)
            {
                return token.ToString();
            }

            if (next == '#')
            {
                while (next is not ('\n' or -1))
                {
                    try
                    {
                        next = reader.ReadByte();
                    }
                    catch (EndOfStreamException)
                    {
                        return token.ToString();
                    }
                }

                continue;
            }

            if (char.IsWhiteSpace((char)next))
            {
                // Leading whitespace is skipped; whitespace after a token ends it, and the
                // single byte after the last header token is the separator PPM promises.
                if (token.Length > 0)
                {
                    return token.ToString();
                }

                continue;
            }

            token.Append((char)next);
        }
    }
}
