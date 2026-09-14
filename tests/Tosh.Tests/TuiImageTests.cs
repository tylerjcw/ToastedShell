using System.Text;
using Tosh.Tui;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

/// <summary>
/// Pictures in a terminal, drawn in half-blocks (<c>TUI-0025</c>).
/// </summary>
public sealed class TuiImageTests
{
    /// <summary>A picture of solid colours, one byte triple per pixel.</summary>
    private static TuiPixels Of(int width, int height, params (byte R, byte G, byte B)[] pixels)
    {
        var rgb = new byte[width * height * 3];

        for (var index = 0; index < width * height; index += 1)
        {
            var (red, green, blue) = pixels[index % pixels.Length];

            rgb[index * 3] = red;
            rgb[(index * 3) + 1] = green;
            rgb[(index * 3) + 2] = blue;
        }

        return new TuiPixels(width, height, rgb);
    }

    private static TuiBuffer Render(TuiWidget widget, int width, int height)
    {
        var buffer = new TuiBuffer(new TuiSize(width, height));
        var bounds = new TuiRect(0, 0, width, height);

        widget.Measure(TuiConstraints.From(new TuiSize(width, height)));
        widget.Arrange(bounds);
        widget.Paint(new TuiSurface(buffer, bounds));

        return buffer;
    }

    [Fact]
    public void One_cell_carries_two_pixels()
    {
        // The whole idea: an upper half block with the top pixel in front and the bottom
        // pixel behind, so a cell grid shows twice as many rows as it has.
        var image = new TuiImage(Of(1, 2, (255, 0, 0), (0, 0, 255))) { Fit = TuiImageFit.Stretch };
        var cell = Render(image, 1, 1)[0, 0];

        Assert.Equal("▀", cell.Text);
        Assert.Equal("#ff0000", cell.Style.Foreground);
        Assert.Equal("#0000ff", cell.Style.Background);
    }

    [Fact]
    public void A_picture_with_no_pixels_draws_what_it_was_told_to_say()
    {
        var image = new TuiImage { Placeholder = "no preview" };

        Assert.Equal("no preview", Render(image, 20, 1).RowText(0).TrimEnd());
    }

    [Fact]
    public void Letterboxing_keeps_the_shape_and_leaves_the_rest_alone()
    {
        // A wide picture in a square canvas: full width, bars above and below, and the bars
        // are left unpainted so it sits on whatever is behind it rather than on black.
        var image = new TuiImage(Of(8, 2, (255, 255, 255))) { Fit = TuiImageFit.Letterbox };
        var buffer = Render(image, 8, 4);

        var painted = Enumerable.Range(0, 4)
            .Where(row => buffer[0, row].Style.Foreground is not null)
            .ToArray();

        Assert.Equal([1, 2], painted);
    }

    [Fact]
    public void Cropping_fills_the_room_and_loses_the_edges()
    {
        var image = new TuiImage(Of(8, 2, (255, 255, 255))) { Fit = TuiImageFit.Crop };
        var buffer = Render(image, 8, 4);

        Assert.All(Enumerable.Range(0, 4), row => Assert.NotNull(buffer[0, row].Style.Foreground));
    }

    [Fact]
    public void A_cropped_picture_is_centred_rather_than_pinned_to_the_corner()
    {
        // The bug a rectangle would have hidden: a cropped picture is bigger than the
        // canvas and sits at a negative offset, and clamping that to zero crops only the
        // far edges.
        // Twice as wide as the canvas once scaled to fill it, so half a canvas-width hangs
        // off each side and the middle is what survives.
        var image = new TuiImage(Of(4, 2, (0, 0, 0))) { Fit = TuiImageFit.Crop };
        var placed = image.Place(image.Pixels!, new TuiSize(2, 2));

        Assert.Equal(-1, placed.Left);
        Assert.Equal(4, placed.Width);
        Assert.Equal(0, placed.Top);
        Assert.Equal(2, placed.Height);
    }

    [Fact]
    public void A_picture_is_two_pixels_tall_per_cell_when_asked_how_big_it_is()
    {
        var image = new TuiImage(Of(10, 7, (1, 2, 3)));

        var wanted = image.Measure(TuiConstraints.Unbounded);

        Assert.Equal(10, wanted.Width);
        Assert.Equal(4, wanted.Height);
    }

    [Fact]
    public void Binary_ppm_is_read_because_every_image_tool_can_write_it()
    {
        var header = Encoding.ASCII.GetBytes("P6\n# made by a test\n2 1\n255\n");
        var body = new byte[] { 255, 0, 0, 0, 255, 0 };

        using var stream = new MemoryStream([.. header, .. body]);

        var pixels = TuiPixels.FromPpm(stream);

        Assert.NotNull(pixels);
        Assert.Equal(2, pixels!.Width);
        Assert.Equal(1, pixels.Height);
        Assert.Equal("#ff0000", pixels.HexAt(0, 0));
        Assert.Equal("#00ff00", pixels.HexAt(1, 0));
    }

    [Fact]
    public void Sixteen_bits_a_sample_is_read_because_that_is_what_imagemagick_writes()
    {
        // The first real image tried came back at 65535 and was refused, which is a
        // preview pane that says "no preview" for every PNG on the machine.
        var header = Encoding.ASCII.GetBytes("P6\n1 1\n65535\n");
        var body = new byte[] { 0xff, 0x00, 0x80, 0x00, 0x00, 0x00 };

        using var stream = new MemoryStream([.. header, .. body]);

        var pixels = TuiPixels.FromPpm(stream);

        Assert.NotNull(pixels);
        Assert.Equal("#ff8000", pixels!.HexAt(0, 0));
    }

    [Theory]
    [InlineData("P3\n2 1\n255\n")]
    [InlineData("P6\n2 1\n99999\n")]
    [InlineData("not an image at all")]
    public void Anything_that_is_not_binary_ppm_is_refused_rather_than_misread(string text)
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(text));

        Assert.Null(TuiPixels.FromPpm(stream));
    }

    [Fact]
    public void A_truncated_picture_is_refused_rather_than_half_drawn()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("P6\n4 4\n255\nshort"));

        Assert.Null(TuiPixels.FromPpm(stream));
    }

    [Fact]
    public void A_path_with_no_loader_leaves_a_placeholder_rather_than_throwing()
    {
        var was = TuiImage.Loader;

        try
        {
            TuiImage.Loader = null;

            var image = new TuiImage { Path = "/nowhere/at/all.png", Placeholder = "no preview" };

            Assert.Null(image.Pixels);
            Assert.Equal("no preview", Render(image, 20, 1).RowText(0).TrimEnd());
        }
        finally
        {
            TuiImage.Loader = was;
        }
    }

    [Fact]
    public void A_path_is_turned_into_pixels_by_whatever_the_host_installed()
    {
        var was = TuiImage.Loader;

        try
        {
            TuiImage.Loader = path => path.EndsWith(".ppm", StringComparison.Ordinal)
                ? Of(1, 2, (9, 9, 9))
                : null;

            Assert.NotNull(new TuiImage { Path = "picture.ppm" }.Pixels);
            Assert.Null(new TuiImage { Path = "picture.docx" }.Pixels);
        }
        finally
        {
            TuiImage.Loader = was;
        }
    }
}
