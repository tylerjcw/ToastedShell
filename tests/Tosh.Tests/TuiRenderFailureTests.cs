using Tosh.Runtime;
using Tosh.Tui;
using Tosh.Tui.Declarative;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

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

    /// <summary>
    /// A binding that throws costs its own widget and nothing else.
    /// </summary>
    /// <remarks>
    /// Catching the whole frame is not enough on its own. Dropping a frame keeps the last
    /// good one, and there is no last good one when the failing binding is on the first —
    /// so a screen whose title was misspelled opened as an empty rectangle with a banner
    /// under it, which says far less than the screen with one blank line in it.
    /// </remarks>
    [Fact]
    public void One_binding_that_throws_costs_one_widget_and_not_the_tree()
    {
        var broken = new Source();
        var working = new Source();

        var first = new TuiTextWidget { TextSource = broken };
        var second = new TuiTextWidget { TextSource = working };
        var root = new TuiStack(TuiOrientation.Vertical) { Items = [first, second] };

        var failures = new List<Exception>();

        TuiBindings.Apply(
            root,
            (source, _) => ReferenceEquals(source, broken)
                ? throw new InvalidOperationException("no such property")
                : "read fresh",
            values: null,
            failures.Add);

        // The one after it is asked, which is the point: the walk carries on past a
        // failure rather than unwinding out of the tree.
        Assert.Equal("read fresh", second.Text);
        Assert.Equal(string.Empty, first.Text);
        Assert.Equal("no such property", Assert.Single(failures).Message);
    }

    /// <summary>
    /// A binding that does something on the way to its answer shows the answer.
    /// </summary>
    /// <remarks>
    /// In TōSh a bare statement emits, so a side effect inside a binding is a value on the
    /// way past — and a widget showing one string was stringifying the whole collection as
    /// <c>System.Object[]</c>. That is what `examples/system-monitor.tosh` displayed under
    /// its meters, because its summary pushes two samples before returning its text.
    /// </remarks>
    [Fact]
    public void A_widget_that_shows_one_thing_takes_the_last_thing_produced()
    {
        var widget = new TuiTextWidget { TextSource = new Source() };

        TuiBindings.Apply(
            widget,
            (_, _) => new object?[] { "a side effect", "another", "the answer" },
            values: null);

        Assert.Equal("the answer", widget.Text);
    }

    /// <summary>A widget that shows many things still gets all of them.</summary>
    /// <remarks>
    /// The other half, and the reason the invoker collects everything in the first place:
    /// `Lines`, `List`, `Table`, `Spark` and `Bars` had all been showing their final element
    /// and nothing else (<c>TUI-0008</c>).
    /// </remarks>
    [Fact]
    public void A_widget_that_shows_many_things_still_gets_all_of_them()
    {
        var widget = new TuiLines { LinesSource = new Source() };

        TuiBindings.Apply(
            widget,
            (_, _) => new object?[] { "first", "second", "third" },
            values: null);

        Assert.Equal(["first", "second", "third"], widget.Lines.Select(line => line.Text));
    }

    /// <summary>With nobody listening, a binding still throws where it always did.</summary>
    /// <remarks>
    /// A screen wires the sink; a test or a script driving widgets itself does not, and
    /// swallowing a mistake there would hide it with nothing drawing a banner to say so.
    /// </remarks>
    [Fact]
    public void A_binding_with_no_reporter_behind_it_is_left_to_throw()
    {
        var widget = new TuiTextWidget { TextSource = new Source() };

        Assert.Throws<InvalidOperationException>(() => TuiBindings.Apply(
            widget,
            (_, _) => throw new InvalidOperationException("nothing is catching this"),
            values: null));
    }

    /// <summary>A stand-in for a script function, told apart by identity alone.</summary>
    private sealed class Source : IShellCallable
    {
        public string CallableName => "source";

        public int RequiredParameterCount => 0;

        public int? MaximumParameterCount => 0;

        public IAsyncEnumerable<object?> InvokeAsync(CommandContext context)
            => throw new NotSupportedException("the invoker is stubbed by the test");
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
