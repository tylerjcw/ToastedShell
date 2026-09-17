using Tosh.Tui;
using Tosh.Tui.Declarative;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

/// <summary>
/// Saying that something is still happening, without a thread (<c>TUI-0020</c>).
/// </summary>
public sealed class TuiSpinnerTests
{
    /// <summary>A spinner whose clock a test decides.</summary>
    private static TuiSpinner At(TimeSpan elapsed, TuiSpinnerStyle? style = null)
    {
        var spinner = new TuiSpinner { Clock = () => elapsed };

        if (style is not null)
        {
            spinner.Style = style;
        }

        return spinner;
    }

    private static string[] Render(TuiWidget widget, int width, int height)
    {
        var buffer = new TuiBuffer(new TuiSize(width, height));
        var bounds = new TuiRect(0, 0, width, height);

        widget.Measure(TuiConstraints.From(new TuiSize(width, height)));
        widget.Arrange(bounds);
        widget.Paint(new TuiSurface(buffer, bounds));

        return [.. Enumerable.Range(0, height).Select(row => buffer.RowText(row).TrimEnd())];
    }

    [Fact]
    public void The_frame_is_a_function_of_the_clock()
    {
        // Not a counter something advances. Nothing has to tick it, and two spinners on one
        // screen cannot disagree about whose turn it is.
        var style = TuiSpinnerStyle.Dots;

        Assert.Equal(style.Frames[0], At(TimeSpan.Zero).CurrentFrame);
        Assert.Equal(style.Frames[1], At(style.Interval).CurrentFrame);
        Assert.Equal(style.Frames[3], At(style.Interval * 3).CurrentFrame);
    }

    [Fact]
    public void And_wraps_round_rather_than_running_off_the_end()
    {
        var style = TuiSpinnerStyle.Dots;
        var full = style.Interval * style.Frames.Count;

        Assert.Equal(style.Frames[0], At(full).CurrentFrame);
        Assert.Equal(style.Frames[2], At(full + (style.Interval * 2)).CurrentFrame);
    }

    [Fact]
    public void Two_spinners_of_the_same_style_turn_together()
    {
        var elapsed = TimeSpan.FromMilliseconds(345);

        Assert.Equal(At(elapsed).CurrentFrame, At(elapsed).CurrentFrame);
    }

    [Theory]
    [InlineData("dots")]
    [InlineData("line")]
    [InlineData("arc")]
    [InlineData("circle")]
    [InlineData("bounce")]
    [InlineData("blocks")]
    [InlineData("ellipsis")]
    [InlineData("ascii")]
    public void Every_style_has_frames_and_a_pace(string name)
    {
        var style = TuiSpinnerStyle.Named(name);

        Assert.True(style.Frames.Count > 1, $"'{name}' has nothing to turn through.");
        Assert.True(style.Interval > TimeSpan.Zero, $"'{name}' has no pace.");
        Assert.All(style.Frames, frame => Assert.Equal(style.Width, TuiTextMeasure.MeasureWidth(frame)));
    }

    [Fact]
    public void The_one_that_works_anywhere_uses_nothing_exotic()
    {
        // Every other style is above U+2000 and is a row of boxes on a terminal without the
        // font for it. This is the fallback that never is.
        Assert.All(
            TuiSpinnerStyle.Ascii.Frames,
            frame => Assert.All(frame, character => Assert.InRange(character, ' ', '~')));
    }

    [Fact]
    public void A_name_nobody_recognises_still_spins()
    {
        // A screen with one mistyped spinner should still run, the same way an unrecognised
        // chord does nothing rather than ending the screen.
        Assert.Equal(TuiSpinnerStyle.Dots, TuiSpinnerStyle.Named("wobble"));
        Assert.Equal(TuiSpinnerStyle.Dots, TuiSpinnerStyle.Named(null));
    }

    [Fact]
    public void A_stopped_spinner_shows_what_it_was_told_to_and_asks_for_nothing()
    {
        var spinner = At(TimeSpan.Zero);

        spinner.Idle = "✓";
        spinner.IsSpinning = false;

        Assert.Equal("✓", spinner.CurrentFrame);
        Assert.Null(spinner.Pace);
    }

    [Fact]
    public void Whether_it_is_turning_can_be_asked_rather_than_set()
    {
        var busy = true;
        var spinner = At(TimeSpan.Zero);

        spinner.SpinningWhen = () => busy;

        Assert.NotNull(spinner.Pace);

        busy = false;

        Assert.Null(spinner.Pace);
    }

    [Fact]
    public void The_text_sits_at_the_style_width_so_it_does_not_jitter()
    {
        // Several of these characters are "ambiguous width" and render as two columns on
        // some terminals, which is the case that would move the text as it turned.
        var narrow = At(TimeSpan.Zero, TuiSpinnerStyle.Dots);
        var wide = At(TimeSpan.Zero, TuiSpinnerStyle.Ellipsis);

        narrow.Text = "working";
        wide.Text = "working";

        Assert.Equal("⠋ working", Render(narrow, 20, 1)[0]);
        Assert.Equal("    working", Render(wide, 20, 1)[0]);
    }

    [Fact]
    public void A_screen_with_a_spinner_asks_to_be_redrawn_without_being_told_to()
    {
        // It asks for redraws rather than starting a thread, so `tui run` animates it
        // without the author knowing that a spinner has a pace.
        var spinner = new TuiSpinner("working");

        using var screen = new TuiDeclarativeScreen(spinner, []);

        Assert.Equal(TuiSpinnerStyle.Dots.Interval, screen.RefreshInterval);

        spinner.IsSpinning = false;

        // And stops asking, so a finished screen costs nothing again.
        Assert.Null(screen.RefreshInterval);
    }

    [Fact]
    public void A_screen_takes_the_sooner_of_its_own_interval_and_a_spinners()
    {
        var spinner = new TuiSpinner("working") { Style = TuiSpinnerStyle.Ellipsis };
        var root = new TuiStack(TuiOrientation.Vertical) { Items = [spinner] };

        using var slow = new TuiDeclarativeScreen(root, [], refreshInterval: TimeSpan.FromSeconds(1));

        Assert.Equal(TuiSpinnerStyle.Ellipsis.Interval, slow.RefreshInterval);

        using var fast = new TuiDeclarativeScreen(
            new TuiStack(TuiOrientation.Vertical) { Items = [new TuiSpinner("x")] },
            [],
            refreshInterval: TimeSpan.FromMilliseconds(10));

        Assert.Equal(TimeSpan.FromMilliseconds(10), fast.RefreshInterval);
    }

    [Fact]
    public void A_spinner_written_in_markup_picks_its_style_by_name()
    {
        var widget = TuiTreeBuilder.Build(new Dictionary<string, object?>
        {
            ["Spinner"] = "working",
            ["Frames"] = "ascii",
            ["Idle"] = "done",
        });

        var spinner = Assert.IsType<TuiSpinner>(widget);

        Assert.Equal("working", spinner.Text);
        Assert.Equal(TuiSpinnerStyle.Ascii, spinner.Style);
        Assert.Equal("done", spinner.Idle);
    }
}
