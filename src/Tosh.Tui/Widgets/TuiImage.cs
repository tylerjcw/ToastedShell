using Tosh.Runtime;
using Tosh.Tui.Rendering;

namespace Tosh.Tui.Widgets;

/// <summary>How a picture is fitted to the room it is given.</summary>
public enum TuiImageFit
{
    /// <summary>Whole picture, right shape, blank bars where it does not reach.</summary>
    Letterbox,

    /// <summary>Fills the room, right shape, edges cut off.</summary>
    Crop,

    /// <summary>Fills the room exactly, wrong shape.</summary>
    Stretch,
}

/// <summary>
/// A picture, drawn in half-blocks.
/// </summary>
/// <remarks>
/// <para>
/// <c>yazi</c> previews an image, a video frame and a page of a PDF in the terminal,
/// because Kitty, WezTerm, Ghostty and iTerm2 all accept pixels over the same channel they
/// accept text on. A framework that cannot do that has decided a file browser's third pane
/// is a list of bytes (<c>TUI-0025</c>).
/// </para>
/// <para>
/// This is the floor of it, and the part that always works: one cell is two pixels, an
/// upper half block with the top pixel's colour in front and the bottom pixel's behind. No
/// protocol to detect, no placement to erase, nothing that a plain-text snapshot cannot
/// capture — and it degrades to a recognisable shape on anything that does truecolour.
/// </para>
/// <para>
/// The widget knows pixels and nothing else. What turns a path into pixels is
/// <see cref="Loader"/>, installed by the host, so the toolkit has no image dependency and
/// a video frame is the same kind of thing as a PNG.
/// </para>
/// </remarks>
public sealed class TuiImage : TuiWidget
{
    /// <summary>The upper half of a cell, so one cell carries two pixels.</summary>
    private const string HalfBlock = "▀";

    /// <summary>
    /// The most pixels a cell is assumed to be worth when handing the terminal a picture.
    /// </summary>
    /// <remarks>
    /// Generous — larger than any real font, HiDPI included — because the cost of being
    /// wrong upwards is a slightly soft picture and the cost of being wrong downwards is a
    /// visibly blocky one. What it buys is not sending the original: a 1024-pixel photograph
    /// in a thirteen-cell box is four megabytes of base64 down a pipe, to be scaled to two
    /// hundred pixels at the other end.
    /// </remarks>
    private const int CellWidth = 24;

    private const int CellHeight = 48;

    /// <summary>
    /// How pictures reach this terminal. Installed by the host, which knows what it is
    /// attached to.
    /// </summary>
    /// <remarks>
    /// Half blocks by default, because they are the thing that always works and a toolkit
    /// with no host is a toolkit that cannot know any better.
    /// </remarks>
    public static TuiGraphicsProtocol Protocol
    {
        get => TuiGraphics.Protocol;
        set => TuiGraphics.Protocol = value;
    }

    /// <summary>The next id to hand a picture that needs one.</summary>
    private static int _nextId;

    private readonly int _id = Interlocked.Increment(ref _nextId);

    /// <summary>
    /// What turns a path into pixels. Installed by the host; null in a bare toolkit.
    /// </summary>
    /// <remarks>
    /// The hook pattern the table's columns and the tree's glyphs already use. A widget
    /// that shelled out to <c>magick</c> would be a widget that knows what a shell is.
    /// </remarks>
    public static Func<string, TuiPixels?>? Loader { get; set; }

    public TuiImage(TuiPixels? pixels = null)
    {
        Pixels = pixels;
    }

    /// <summary>The picture, or null while there is nothing to show.</summary>
    /// <remarks>
    /// Read lazily where nothing has offered to load off the loop: a widget used on its own
    /// still works, and the first thing that asks what it looks like is what makes it load.
    /// </remarks>
    public TuiPixels? Pixels
    {
        get
        {
            if (!Deferred && Pending is { } waiting)
            {
                Accept(waiting, waiting.Length > 0 ? Loader?.Invoke(waiting) : null);
            }

            return field;
        }

        set
        {
            field = value;
            _loaded = Path;
        }
    }

    /// <summary>What <see cref="Pixels"/> currently holds, which may not be what was asked for.</summary>
    private string? _loaded;

    /// <summary>
    /// Whether something else has taken responsibility for loading.
    /// </summary>
    /// <remarks>
    /// Set by whatever owns the tree when it can load off the render loop. Until then the
    /// widget loads for itself, because a widget that needed a host to show a picture would
    /// be a widget that cannot be used without one.
    /// </remarks>
    internal bool Deferred { get; set; }

    /// <summary>The path that has been asked for and not yet loaded, if there is one.</summary>
    internal string? Pending
        => string.Equals(_loaded, Path, StringComparison.Ordinal) ? null : Path ?? string.Empty;

    /// <summary>Whether the picture asked for has not arrived yet.</summary>
    public bool IsLoading => Pending is { Length: > 0 };

    /// <summary>
    /// Takes the pixels for a path, if that is still the path being asked for.
    /// </summary>
    /// <remarks>
    /// A reader arrowing down a directory asks for five files while the first is still
    /// being read. Only the last of them is wanted, and a result that arrives for a file
    /// nobody is looking at any more is dropped rather than drawn.
    /// </remarks>
    internal void Accept(string? path, TuiPixels? pixels)
    {
        if (!string.Equals(path, Path ?? string.Empty, StringComparison.Ordinal))
        {
            return;
        }

        Pixels = pixels;
    }

    /// <summary>Where the picture comes from, when it comes from a file.</summary>
    /// <remarks>
    /// Setting this asks <see cref="Loader"/> straight away. A path that loads to nothing —
    /// no loader, a missing file, a format the loader does not know — leaves
    /// <see cref="Pixels"/> null and the widget draws its placeholder, which is what a
    /// preview pane should do for a file it cannot preview.
    /// </remarks>
    public string? Path
    {
        get;
        set
        {
            // Only when it actually changes. A pull binding re-reads this before every
            // frame, and a setter that reloaded each time would run a video through ffmpeg
            // once per keystroke.
            if (string.Equals(field, value, StringComparison.Ordinal))
            {
                return;
            }

            field = value;
        }
    }

    /// <summary>A script function supplying the path, re-read on every redraw.</summary>
    /// <remarks>
    /// What makes a preview pane a preview pane: the list's selection is the question and
    /// this is the answer, so nothing has to be wired between them.
    /// </remarks>
    public IShellCallable? PathSource { get; set; }

    /// <summary>How the picture is fitted to the room it is given.</summary>
    public TuiImageFit Fit { get; set; } = TuiImageFit.Letterbox;

    /// <summary>What is drawn when there is no picture.</summary>
    public string Placeholder { get; set; } = string.Empty;

    /// <summary>What is drawn while one is still being read.</summary>
    /// <remarks>
    /// Distinct from <see cref="Placeholder"/>, which says a file cannot be previewed. A
    /// video being pulled apart by <c>ffmpeg</c> has not failed; it is just slower than a
    /// keystroke.
    /// </remarks>
    public string Loading { get; set; } = "\u2026";

    public TuiStyle Style { get; set; }

    /// <inheritdoc />
    public override object? Value => Path;

    /// <inheritdoc />
    /// <remarks>
    /// One cell is two pixels tall, so a picture's natural size in cells is its width by
    /// half its height — clamped, because a photograph's natural size is larger than any
    /// terminal and a pane that asked for it would get all of one.
    /// </remarks>
    protected override TuiSize MeasureCore(TuiConstraints constraints)
    {
        if (Pixels is { IsEmpty: false } pixels)
        {
            return constraints.Constrain(new TuiSize(pixels.Width, (pixels.Height + 1) / 2));
        }

        var text = IsLoading ? Loading : Placeholder;

        return constraints.Constrain(new TuiSize(TextMeasure.MeasureWidth(text), text.Length > 0 ? 1 : 0));
    }

    /// <inheritdoc />
    public override void Draw(TuiSurface surface)
    {
        if (Pixels is not { IsEmpty: false } pixels)
        {
            var text = IsLoading ? Loading : Placeholder;

            if (text.Length > 0)
            {
                surface.DrawText(0, 0, TextMeasure.Elide(text, surface.Width), Style);
            }

            return;
        }

        if (Protocol != TuiGraphicsProtocol.HalfBlocks)
        {
            DrawPixels(surface, pixels);
            return;
        }

        // The canvas is the cells doubled up: two pixels to a cell, top and bottom.
        var canvas = new TuiSize(surface.Width, surface.Height * 2);

        if (canvas.Width == 0 || canvas.Height == 0)
        {
            return;
        }

        var placed = Place(pixels, canvas);

        for (var row = 0; row < surface.Height; row += 1)
        {
            for (var column = 0; column < surface.Width; column += 1)
            {
                var top = Sample(pixels, placed, column, row * 2);
                var bottom = Sample(pixels, placed, column, (row * 2) + 1);

                if (top is null && bottom is null)
                {
                    // Outside the picture altogether: left alone rather than painted black,
                    // so a letterboxed image sits on whatever is behind it.
                    continue;
                }

                surface.DrawText(column, row, HalfBlock, new TuiStyle(top ?? bottom, bottom ?? top));
            }
        }
    }

    /// <summary>
    /// Hands the terminal the picture itself, over a rectangle of cleared cells.
    /// </summary>
    /// <remarks>
    /// The cells underneath are blanked rather than left as they were. A terminal draws an
    /// image over the text, so anything left there shows through wherever the picture does
    /// not reach — and when the picture is taken away, the cells are what the reader is
    /// left looking at.
    /// </remarks>
    private void DrawPixels(TuiSurface surface, TuiPixels pixels)
    {
        // Fitted on the same half-cell canvas the blocks use, because a cell is about twice
        // as tall as it is wide and a fit that treats one as square stretches every picture
        // vertically. Switching protocols should change how a picture is drawn, not where.
        var placed = Place(pixels, new TuiSize(surface.Width, surface.Height * 2));

        if (placed.Width <= 0 || placed.Height <= 0)
        {
            return;
        }

        // Back into whole cells. Rounded outwards, so a picture an odd number of half-cells
        // tall keeps all of itself rather than losing its last row.
        var top = placed.Top / 2;
        var rows = (placed.Top + placed.Height + 1) / 2 - top;

        var region = new TuiRect(
            Math.Max(0, placed.Left),
            Math.Max(0, top),
            Math.Min(placed.Width, surface.Width),
            Math.Min(rows, surface.Height));

        surface.Clip(region).Fill(Style);

        // The cut is measured on the half-cell canvas the fit was decided on, so the part of
        // the picture that survives is the part those cells were showing.
        var cut = Cut(pixels, placed, new TuiRect(region.Left, region.Top * 2, region.Width, region.Height * 2));

        surface.Place(_id, region, cut.Resampled(
            Math.Min(cut.Width, region.Width * CellWidth),
            Math.Min(cut.Height, region.Height * CellHeight)));
    }

    /// <summary>
    /// The part of the picture a cropped placement should carry.
    /// </summary>
    /// <remarks>
    /// Cropping is done here rather than by the terminal because the terminal would scale
    /// whatever it is given into the box it is given: handing it the whole picture and a
    /// smaller box is a squashed picture, not a cropped one.
    /// </remarks>
    private static TuiPixels Cut(
        TuiPixels pixels,
        (int Left, int Top, int Width, int Height) placed,
        TuiRect region)
    {
        if (placed.Left >= 0 && placed.Top >= 0 && placed.Width == region.Width && placed.Height == region.Height)
        {
            return pixels;
        }

        var left = (region.Left - placed.Left) * pixels.Width / placed.Width;
        var top = (region.Top - placed.Top) * pixels.Height / placed.Height;
        var width = Math.Max(1, region.Width * pixels.Width / placed.Width);
        var height = Math.Max(1, region.Height * pixels.Height / placed.Height);

        var rgb = new byte[width * height * 3];

        for (var y = 0; y < height; y += 1)
        {
            for (var x = 0; x < width; x += 1)
            {
                var (red, green, blue) = pixels[left + x, top + y];
                var offset = ((y * width) + x) * 3;

                rgb[offset] = red;
                rgb[offset + 1] = green;
                rgb[offset + 2] = blue;
            }
        }

        return new TuiPixels(width, height, rgb);
    }

    /// <summary>Where the picture sits on the canvas, in canvas pixels.</summary>
    /// <remarks>
    /// Not a <see cref="TuiRect"/>: a cropped picture is bigger than the canvas and sits at
    /// a negative offset, and a rectangle clamps that to zero — which pins the picture to
    /// the top-left corner and crops only the far edges.
    /// </remarks>
    internal (int Left, int Top, int Width, int Height) Place(TuiPixels pixels, TuiSize canvas)
    {
        if (Fit == TuiImageFit.Stretch)
        {
            return (0, 0, canvas.Width, canvas.Height);
        }

        // Which axis binds, as a cross-multiplication so the arithmetic stays whole:
        // fitting inside takes the smaller ratio and leaves bars, filling takes the larger
        // and overflows.
        var wide = pixels.Width * canvas.Height;
        var tall = pixels.Height * canvas.Width;
        var byWidth = Fit == TuiImageFit.Letterbox ? tall <= wide : tall >= wide;

        var width = byWidth ? canvas.Width : Math.Max(1, pixels.Width * canvas.Height / pixels.Height);
        var height = byWidth ? Math.Max(1, pixels.Height * canvas.Width / pixels.Width) : canvas.Height;

        return ((canvas.Width - width) / 2, (canvas.Height - height) / 2, width, height);
    }

    /// <summary>The colour at one canvas pixel, or null where the picture does not reach.</summary>
    private static string? Sample(
        TuiPixels pixels,
        (int Left, int Top, int Width, int Height) placed,
        int column,
        int row)
    {
        if (placed.Width <= 0 || placed.Height <= 0)
        {
            return null;
        }

        var x = column - placed.Left;
        var y = row - placed.Top;

        if (x < 0 || y < 0 || x >= placed.Width || y >= placed.Height)
        {
            return null;
        }

        return pixels.HexAt(x * pixels.Width / placed.Width, y * pixels.Height / placed.Height);
    }
}
