namespace Tosh.Tui.Rendering;

/// <summary>
/// Telling the terminal that a frame is arriving in pieces, and when it is whole.
/// </summary>
/// <remarks>
/// <para>
/// A frame reaches the terminal as a stream of cursor moves and runs of text. The terminal
/// is free to draw as it reads, so a reader can see a frame half-applied — the top half of
/// a table from this frame and the bottom half from the last. On a screen refreshing once a
/// second, that is a visible tear on every tick.
/// </para>
/// <para>
/// Mode 2026 says "hold what you have until I say I have finished". Between the two, the
/// terminal keeps showing the previous frame; at the end it swaps to the new one whole. The
/// frame is not made smaller or faster — it stops being seen in the middle of arriving.
/// </para>
/// <para>
/// Emitted without asking whether the terminal supports it. A terminal that does not know a
/// private mode ignores the request, which is what the escape is defined to do, and asking
/// would mean reading the answer out of the same stream as the keyboard — the race
/// <see cref="TuiColors.Detect"/> avoids for the same reason.
/// </para>
/// </remarks>
public static class TuiSynchronizedOutput
{
    /// <summary>Hold the display until the update ends.</summary>
    public const string Begin = "\x1b[?2026h";

    /// <summary>Show everything written since the update began.</summary>
    public const string End = "\x1b[?2026l";

    /// <summary>Whether frames should be wrapped, by the environment's account.</summary>
    /// <remarks>
    /// An opt-out rather than an opt-in: a terminal that ignores the mode is unharmed, so
    /// the default that helps most readers is the one that does not need setting.
    /// <c>TOSH_TUI_SYNC=0</c> is for a terminal that claims the mode and mishandles it,
    /// which is the only case where a reader is worse off than without it.
    /// </remarks>
    public static bool Detect(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return environment("TOSH_TUI_SYNC") is not ("0" or "off" or "false" or "no");
    }

    /// <summary>
    /// A frame the terminal will show whole, or nothing at all.
    /// </summary>
    /// <remarks>
    /// An empty frame stays empty. A diff that found nothing to change is most of the frames
    /// a waiting screen produces, and bracketing nothing would put two escapes on the wire
    /// every tick to announce that nothing happened.
    /// </remarks>
    public static string Wrap(string frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        return frame.Length == 0 ? frame : Begin + frame + End;
    }
}
