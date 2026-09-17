using Tosh.Tui;
using Tosh.Tui.Rendering;

namespace Tosh.Tests;

/// <summary>
/// What a screen costs while nobody is touching it (<c>TUI-0012</c>).
/// </summary>
/// <remarks>
/// The budget's least negotiable line: a screen with no refresh interval should cost nothing
/// measurable while idle. It is easy to get wrong in a way nobody notices — a loop that
/// redraws on every wait slice looks identical on screen and burns a core.
/// </remarks>
public sealed class TuiIdleCostTests
{
    /// <summary>
    /// An idle screen is not redrawn, however long it waits.
    /// </summary>
    /// <remarks>
    /// The loop waits in slices rather than blocking on the keyboard, so that a signal can
    /// be noticed (<c>TUI-0010</c>). The slices are inside the wait: it returns when
    /// something has actually happened, so a hundred of them cost a hundred cheap polls and
    /// not a hundred frames.
    /// </remarks>
    [Fact]
    public void An_idle_screen_is_drawn_once_and_then_left_alone()
    {
        var host = new PollingHost(quietPolls: 40);
        var screen = new CountingScreen();

        TuiApplication.Run(host, screen);

        // One frame for the whole session: the screen opens, forty quiet waits change
        // nothing, and the key that arrives ends it before there is anything to redraw.
        Assert.Equal(1, screen.Renders);

        // And the forty quiet polls really did happen, so this is not passing by never
        // having waited at all.
        Assert.Equal(41, host.Polls);
    }

    /// <summary>Nothing reaches the terminal while nothing is changing.</summary>
    /// <remarks>
    /// The other half of the same property, measured where it matters: bytes on the wire.
    /// A redraw that produced an identical frame would still be free of output thanks to
    /// the diff, but it would have cost the frame.
    /// </remarks>
    [Fact]
    public void An_idle_screen_writes_nothing_after_its_first_frame()
    {
        var host = new PollingHost(quietPolls: 20);

        TuiApplication.Run(host, new CountingScreen());

        // The opening frame, and nothing else with a cell in it. What follows is the
        // escapes that put the terminal back.
        Assert.Equal(1, host.Frames);
    }

    /// <summary>A screen that asks for no refresh gets no tick.</summary>
    [Fact]
    public void A_screen_with_no_interval_is_never_ticked()
    {
        var screen = new CountingScreen();

        TuiApplication.Run(new PollingHost(quietPolls: 40), screen);

        Assert.Equal(0, screen.Ticks);
    }

    /// <summary>A host that reports nothing for a while, then one key that quits.</summary>
    private sealed class PollingHost(int quietPolls) : ITuiHost
    {
        private int _remaining = quietPolls;

        /// <summary>How many times the loop asked for input.</summary>
        public int Polls { get; private set; }

        /// <summary>How many times something was written that contained a cell.</summary>
        public int Frames { get; private set; }

        public bool IsInteractive => true;

        public TuiSize? TryGetSize() => new(40, 6);

        public ConsoleKeyInfo ReadKey(bool intercept = true) => default;

        public bool TryReadPendingKey(out ConsoleKeyInfo key, bool intercept = true)
        {
            key = default;
            return false;
        }

        public TuiInputEvent ReadInput() => default;

        public bool TryReadPendingInput(out TuiInputEvent inputEvent)
        {
            inputEvent = default;
            return false;
        }

        /// <summary>
        /// Answers immediately rather than sleeping out the slice.
        /// </summary>
        /// <remarks>
        /// The default implementation polls and sleeps, which would make this test take as
        /// long as the waits it is counting. What is being measured is how many times the
        /// loop comes back to ask, not how long it is willing to wait.
        /// </remarks>
        public bool TryReadInput(TimeSpan timeout, out TuiInputEvent inputEvent)
        {
            Polls += 1;

            if (_remaining > 0)
            {
                _remaining -= 1;
                inputEvent = default;
                return false;
            }

            inputEvent = TuiInputEvent.FromKey(new ConsoleKeyInfo('q', ConsoleKey.Q, false, false, false));
            return true;
        }

        public void Write(string text)
        {
            // The escapes that enter and leave the alternate screen carry no cells, and a
            // frame that changed nothing writes nothing at all — so counting writes with
            // text in them counts frames.
            if (text.Contains('c', StringComparison.Ordinal))
            {
                Frames += 1;
            }
        }
    }

    /// <summary>A screen that draws one word and quits on any key.</summary>
    private sealed class CountingScreen : ITuiScreen
    {
        public int Renders { get; private set; }

        public int Ticks { get; private set; }

        public TimeSpan? RefreshInterval => null;

        public TuiWake? Wake => null;

        public TuiFrame Render(TuiSize size)
        {
            Renders += 1;

            var buffer = new TuiBuffer(size);

            buffer.DrawText(0, 0, "cell", default);

            return new TuiFrame(buffer);
        }

        public TuiScreenResult HandleInput(TuiInputEvent input) => TuiScreenResult.Exit;

        public TuiScreenResult Tick()
        {
            Ticks += 1;
            return TuiScreenResult.Continue;
        }
    }
}
