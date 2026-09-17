using Tosh.Tui;
using Tosh.Tui.Rendering;

namespace Tosh.Tests;

/// <summary>
/// What happens when drawing a frame throws (<c>TUI-0010</c>).
/// </summary>
/// <remarks>
/// Drawing runs user code as surely as a keystroke does: a pull binding is a script
/// function asked once per frame, and far more often than a key handler is. One that threw
/// used to end the screen and print its diagnostic over the alternate screen on the way
/// out — which the item's own acceptance says must not happen, and which was true for key
/// handlers and not for this.
/// </remarks>
public sealed class TuiRenderFailureTests
{
    [Fact]
    public void A_frame_that_cannot_be_drawn_is_reported_rather_than_thrown()
    {
        var reporter = new TuiHandlerFailureReporter();

        var buffer = reporter.Frame(() => throw new InvalidOperationException("binding exploded"));

        Assert.Null(buffer);

        var canvas = new TuiBuffer(new TuiSize(100, 3));

        reporter.Draw(canvas);

        Assert.Contains("binding exploded", canvas.RowText(2), StringComparison.Ordinal);
    }

    [Fact]
    public void A_frame_that_can_be_drawn_comes_back_whole()
    {
        var reporter = new TuiHandlerFailureReporter();
        var drawn = new TuiBuffer(new TuiSize(10, 1));

        Assert.Same(drawn, reporter.Frame(() => new TuiFrame(drawn)));
    }

    [Fact]
    public void The_last_good_frame_is_copied_rather_than_reused()
    {
        // The writer diffs against what it presented. Handing back the same buffer would
        // emit nothing at all — including the message saying why nothing had changed.
        var presented = new TuiBuffer(new TuiSize(20, 2));

        presented.DrawText(0, 0, "still here", default);

        var copy = presented.Copy();

        Assert.NotSame(presented, copy);
        Assert.Equal("still here", copy.RowText(0).TrimEnd());

        // And it is a copy in both directions: writing the banner onto it does not change
        // what the writer thinks is on screen.
        copy.DrawText(0, 1, "handler failed", default);

        Assert.Equal(string.Empty, presented.RowText(1).TrimEnd());
        Assert.NotEqual(string.Empty, TuiTerminalWriter.Present(presented, copy));
    }

    [Fact]
    public void A_picture_is_not_asked_for_twice_because_a_frame_failed()
    {
        // The copy deliberately carries no placements: the picture is already on screen,
        // and sending it again on every failed frame is how a broken binding becomes a
        // flood down the wire.
        var presented = new TuiBuffer(new TuiSize(20, 4));

        presented.Place(new TuiPlacement(1, 0, 0, 4, 2, new TuiPixels(1, 1, [1, 2, 3])));

        Assert.Empty(presented.Copy().Placements);
    }

    [Fact]
    public void Repeated_failures_are_counted_rather_than_repeated()
    {
        var reporter = new TuiHandlerFailureReporter();

        for (var attempt = 0; attempt < 5; attempt += 1)
        {
            reporter.Frame(() => throw new InvalidOperationException("again"));
        }

        var canvas = new TuiBuffer(new TuiSize(60, 1));

        reporter.Draw(canvas);

        Assert.Contains("x5", canvas.RowText(0), StringComparison.Ordinal);
    }
}
