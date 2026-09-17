using System.Diagnostics;
using Tosh.Tui.Rendering;

namespace Tosh.Tui.Widgets;

/// <summary>One way of saying "still working", as a sequence of frames and a pace.</summary>
/// <remarks>
/// A style carries its own interval because the two are one choice: braille dots read as
/// motion at eighty milliseconds and as a flicker at forty, and a pulsing block wants
/// longer than either.
/// </remarks>
public sealed record TuiSpinnerStyle(IReadOnlyList<string> Frames, TimeSpan Interval)
{
    /// <summary>Braille dots. The default, and the one most tools use.</summary>
    public static TuiSpinnerStyle Dots { get; } = Of(80, "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏");

    /// <summary>A turning line. Four frames, and no character above U+007E.</summary>
    public static TuiSpinnerStyle Line { get; } = Of(120, "|", "/", "-", "\\");

    /// <summary>A turning arc.</summary>
    public static TuiSpinnerStyle Arc { get; } = Of(100, "◜", "◠", "◝", "◞", "◡", "◟");

    /// <summary>A quartered circle.</summary>
    public static TuiSpinnerStyle Circle { get; } = Of(120, "◐", "◓", "◑", "◒");

    /// <summary>A dot bouncing along the bottom.</summary>
    public static TuiSpinnerStyle Bounce { get; } = Of(90, "⠁", "⠂", "⠄", "⡀", "⢀", "⠠", "⠐", "⠈");

    /// <summary>A block growing and shrinking, in the family the gauges already use.</summary>
    public static TuiSpinnerStyle Blocks { get; } = Of(90, "▁", "▂", "▃", "▄", "▅", "▆", "▇", "▆", "▅", "▄", "▃", "▂");

    /// <summary>Three dots filling up, for a pace slow enough to read.</summary>
    public static TuiSpinnerStyle Ellipsis { get; } = Of(300, "   ", ".  ", ".. ", "...");

    /// <summary>
    /// The one that works anywhere.
    /// </summary>
    /// <remarks>
    /// Every other style here is above U+2000 and will be a row of boxes on a terminal
    /// without the font for it. This is the fallback that never is (<c>TUI-0009</c>).
    /// </remarks>
    public static TuiSpinnerStyle Ascii { get; } = Of(120, "-", "\\", "|", "/");

    /// <summary>The style a name refers to, or the default when the name is not one.</summary>
    /// <remarks>
    /// An unrecognised name spins with dots rather than failing: a screen with one
    /// mistyped spinner should still run, the same way an unrecognised chord does nothing
    /// rather than ending the screen.
    /// </remarks>
    public static TuiSpinnerStyle Named(string? name)
        => name?.Trim().ToLowerInvariant() switch
        {
            "line" => Line,
            "arc" => Arc,
            "circle" => Circle,
            "bounce" => Bounce,
            "blocks" or "block" => Blocks,
            "ellipsis" or "dots3" => Ellipsis,
            "ascii" or "plain" => Ascii,
            _ => Dots,
        };

    /// <summary>
    /// How wide this style draws, which is its widest frame.
    /// </summary>
    /// <remarks>
    /// Fixed rather than per-frame, so the text beside a spinner does not jitter left and
    /// right as it turns. Several of these characters are "ambiguous width" and render as
    /// two columns on some terminals, which is exactly the case that would jitter.
    /// </remarks>
    public int Width { get; } = Frames.Count == 0 ? 0 : Frames.Max(TuiTextMeasure.MeasureWidth);

    private static TuiSpinnerStyle Of(int milliseconds, params string[] frames)
        => new(frames, TimeSpan.FromMilliseconds(milliseconds));
}

/// <summary>
/// Says that something is still happening.
/// </summary>
/// <remarks>
/// <para>
/// The frame is a function of the clock rather than a counter something advances, so
/// nothing has to tick it and three spinners on one screen cannot disagree about whose
/// turn it is (<c>TUI-0020</c>).
/// </para>
/// <para>
/// What it does need is redraws, and it asks for them rather than starting a thread: a
/// screen holding a spinning spinner reports a refresh interval of its own accord, so
/// <c>tui run</c> animates it without the author knowing that a spinner has a pace. It
/// stops asking the moment it stops spinning, so a finished screen goes back to costing
/// nothing.
/// </para>
/// </remarks>
public sealed class TuiSpinner : TuiWidget
{
    /// <summary>
    /// One clock for every spinner, so two of them turn together.
    /// </summary>
    /// <remarks>
    /// Separate clocks would drift apart by however long there was between constructing
    /// them, and two spinners a few frames out of step on one screen looks like a fault.
    /// </remarks>
    private static readonly Stopwatch Uptime = Stopwatch.StartNew();

    public TuiSpinner(string text = "")
    {
        Text = text;
    }

    /// <summary>What is said beside the spinner.</summary>
    public string Text { get; set; }

    /// <summary>Which sequence of frames it turns through.</summary>
    public TuiSpinnerStyle Style { get; set; } = TuiSpinnerStyle.Dots;

    /// <summary>Whether it is turning.</summary>
    public bool IsSpinning { get; set; } = true;

    /// <summary>Asked each redraw whether it is turning, when the answer can change.</summary>
    public Func<bool>? SpinningWhen { get; set; }

    /// <summary>What stands in the spinner's place when it has stopped.</summary>
    /// <remarks>
    /// The same width as a frame, so a line of them does not shift when one finishes. A
    /// tick or a cross is what a caller usually wants here.
    /// </remarks>
    public string Idle { get; set; } = " ";

    /// <summary>How the frame itself is drawn.</summary>
    public TuiStyle FrameStyle { get; set; }

    public TuiStyle TextStyle { get; set; }

    /// <summary>Where the clock comes from. Replaceable, so a test can decide the frame.</summary>
    internal Func<TimeSpan> Clock { get; set; } = () => Uptime.Elapsed;

    /// <summary>Whether it is turning right now.</summary>
    public bool Spinning => SpinningWhen?.Invoke() ?? IsSpinning;

    /// <summary>How often this wants redrawing, or null when it has stopped.</summary>
    /// <remarks>
    /// What a screen asks so it can animate without the author setting a refresh interval,
    /// and what it stops asking so a finished screen costs nothing.
    /// </remarks>
    public TimeSpan? Pace => Spinning && Style.Frames.Count > 1 ? Style.Interval : null;

    /// <summary>The frame showing now.</summary>
    public string CurrentFrame
    {
        get
        {
            if (!Spinning || Style.Frames.Count == 0)
            {
                return Idle;
            }

            var ticks = (long)(Clock().TotalMilliseconds / Math.Max(1, Style.Interval.TotalMilliseconds));

            return Style.Frames[(int)(((ticks % Style.Frames.Count) + Style.Frames.Count) % Style.Frames.Count)];
        }
    }

    /// <inheritdoc />
    public override object? Value => Text;

    /// <inheritdoc />
    protected override TuiSize MeasureCore(TuiConstraints constraints)
    {
        var width = Style.Width + (Text.Length > 0 ? TuiTextMeasure.MeasureWidth(Text) + 1 : 0);

        return constraints.Constrain(new TuiSize(width, 1));
    }

    /// <inheritdoc />
    public override void Draw(TuiSurface surface)
    {
        var frame = CurrentFrame;

        surface.DrawText(0, 0, frame, FrameStyle);

        if (Text.Length == 0)
        {
            return;
        }

        // Always at the style's width rather than at this frame's, so the text does not
        // move when a frame happens to be narrower than its neighbours.
        var at = Style.Width + 1;

        surface.DrawText(at, 0, TuiTextMeasure.Elide(Text, Math.Max(0, surface.Width - at)), TextStyle);
    }
}
