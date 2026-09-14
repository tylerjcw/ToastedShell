using System.Text;

namespace Tosh.Tui.Rendering;

/// <summary>How a terminal is asked to draw a picture.</summary>
public enum TuiGraphicsProtocol
{
    /// <summary>Half blocks in the cell grid. Works everywhere that does truecolour.</summary>
    HalfBlocks,

    /// <summary>The Kitty graphics protocol: real pixels, placed and deleted by id.</summary>
    Kitty,
}

/// <summary>
/// Writes pictures the way the terminal understands them.
/// </summary>
/// <remarks>
/// <para>
/// Kitty, Ghostty and WezTerm accept pixels over the same channel they accept text on. The
/// protocol is an APC string — <c>ESC _G</c>, some <c>key=value</c> pairs, a semicolon, a
/// base64 payload, <c>ESC \</c> — and the payload is chunked because a terminal is not
/// obliged to accept an unbounded one.
/// </para>
/// <para>
/// Placements are kept by the terminal until they are deleted, which is the whole reason
/// they carry an id. A picture that moved, closed, or was covered by a dialog has to be
/// taken back, and only something comparing this frame against the last one knows that
/// happened.
/// </para>
/// </remarks>
public static class TuiGraphics
{
    /// <summary>
    /// How much base64 goes in one chunk.
    /// </summary>
    /// <remarks>
    /// The protocol's own limit. Bigger is not allowed and smaller is only more escape
    /// sequences for the same bytes.
    /// </remarks>
    private const int ChunkSize = 4096;

    /// <summary>
    /// What this terminal speaks. Set once by the host, read by anything that needs to know
    /// whether pictures are in play.
    /// </summary>
    /// <remarks>
    /// Half blocks until told otherwise, because they are the thing that always works and a
    /// toolkit with no host cannot know any better.
    /// </remarks>
    public static TuiGraphicsProtocol Protocol { get; set; } = TuiGraphicsProtocol.HalfBlocks;

    /// <summary>Asks the terminal to draw a picture, and to remember it by id.</summary>
    public static string Transmit(TuiPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);

        var pixels = placement.Pixels;
        var payload = Convert.ToBase64String(Bytes(pixels));
        var builder = new StringBuilder();

        // Moved to where the picture goes first: a placement lands at the cursor, and the
        // cursor is wherever the last cell written left it.
        builder.Append($"\x1b[{placement.Row + 1};{placement.Column + 1}H");

        for (var offset = 0; offset < payload.Length; offset += ChunkSize)
        {
            var chunk = payload.AsSpan(offset, Math.Min(ChunkSize, payload.Length - offset));
            var more = offset + ChunkSize < payload.Length ? 1 : 0;

            builder.Append("\x1b_G");

            if (offset == 0)
            {
                // `a=T` transmits and draws in one go. `f=24` is three bytes a pixel, `s`
                // and `v` are the picture's own size, `c` and `r` the cells it is to cover,
                // and `q=2` stops the terminal answering — an answer would arrive in the
                // input stream and be read as a keystroke.
                builder.Append(
                    $"a=T,f=24,i={placement.Id},s={pixels.Width},v={pixels.Height}," +
                    $"c={placement.Columns},r={placement.Rows},q=2,");
            }

            builder.Append($"m={more};").Append(chunk).Append("\x1b\\");
        }

        return builder.ToString();
    }

    /// <summary>Takes a picture back off the screen.</summary>
    public static string Delete(int id) => $"\x1b_Ga=d,d=i,i={id},q=2;\x1b\\";

    /// <summary>Takes every picture off the screen, for handing the terminal back.</summary>
    public static string DeleteAll() => "\x1b_Ga=d,d=A,q=2;\x1b\\";

    private static byte[] Bytes(TuiPixels pixels)
    {
        var bytes = new byte[pixels.Width * pixels.Height * 3];

        for (var y = 0; y < pixels.Height; y += 1)
        {
            for (var x = 0; x < pixels.Width; x += 1)
            {
                var (red, green, blue) = pixels[x, y];
                var offset = ((y * pixels.Width) + x) * 3;

                bytes[offset] = red;
                bytes[offset + 1] = green;
                bytes[offset + 2] = blue;
            }
        }

        return bytes;
    }

    /// <summary>
    /// What the terminal this process is attached to appears to speak.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read from the environment rather than asked for. The protocol does have a query —
    /// send a one-pixel image and read the reply — but the reply arrives in the same input
    /// stream as the keyboard, so asking means racing the reader and risking a stray
    /// <c>ESC_G</c> being typed into whatever has focus. The environment is what these
    /// terminals set about themselves, and a reader whose terminal lies has an override.
    /// </para>
    /// <para>
    /// <c>TERM_PROGRAM</c> is checked before <c>TERM</c>, because a multiplexer rewrites
    /// <c>TERM</c> and the thing underneath is what actually draws.
    /// </para>
    /// </remarks>
    public static TuiGraphicsProtocol Detect(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (Named(environment("TOSH_TUI_GRAPHICS")) is { } told)
        {
            return told;
        }

        // Inside tmux or screen a picture would be drawn over whatever pane the multiplexer
        // decided to put there, because the multiplexer does not know it was sent.
        if (environment("TMUX") is { Length: > 0 } ||
            environment("TERM") is { } multiplexed && multiplexed.StartsWith("screen", StringComparison.OrdinalIgnoreCase))
        {
            return TuiGraphicsProtocol.HalfBlocks;
        }

        if (environment("KITTY_WINDOW_ID") is { Length: > 0 } ||
            environment("GHOSTTY_RESOURCES_DIR") is { Length: > 0 } ||
            environment("GHOSTTY_BIN_DIR") is { Length: > 0 })
        {
            return TuiGraphicsProtocol.Kitty;
        }

        var program = environment("TERM_PROGRAM") ?? string.Empty;
        var term = environment("TERM") ?? string.Empty;

        return Speaks(program) || Speaks(term)
            ? TuiGraphicsProtocol.Kitty
            : TuiGraphicsProtocol.HalfBlocks;

        static bool Speaks(string value)
            => value.Contains("ghostty", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("kitty", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("wezterm", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A protocol a reader named by hand, or null if they named nothing useful.</summary>
    private static TuiGraphicsProtocol? Named(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "kitty" => TuiGraphicsProtocol.Kitty,
            "half" or "halfblocks" or "blocks" or "none" or "off" => TuiGraphicsProtocol.HalfBlocks,
            _ => null,
        };
}
