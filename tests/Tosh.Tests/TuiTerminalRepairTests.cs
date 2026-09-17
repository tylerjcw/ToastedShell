using Tosh.Tui;
using Tosh.Tui.Rendering;

namespace Tosh.Tests;

/// <summary>
/// Putting a terminal back that an earlier TōSh did not (<c>TUI-0010</c>).
/// </summary>
[Collection(TuiGraphicsCollection.Name)]
public sealed class TuiTerminalRepairTests
{
    [Fact]
    public void The_repair_undoes_everything_a_session_switches_on()
    {
        var sane = TuiTerminalRepair.Sane;

        Assert.Contains("\x1b[?1049l", sane, StringComparison.Ordinal);
        Assert.Contains("\x1b[?25h", sane, StringComparison.Ordinal);
        Assert.Contains("\x1b[?1000l", sane, StringComparison.Ordinal);
        Assert.Contains("\x1b[?1006l", sane, StringComparison.Ordinal);
        Assert.Contains("\x1b[0m", sane, StringComparison.Ordinal);

        // And the pictures: one the terminal was asked to draw outlives the program that
        // asked, so a crash during a preview leaves it over the shell you are handed back.
        Assert.Contains("\x1b_Ga=d,d=A", sane, StringComparison.Ordinal);
    }

    [Fact]
    public void The_alternate_screen_is_left_last()
    {
        // Everything else is undone while the screen the mess is on is still up. Leaving
        // first and tidying after writes the escapes onto the reader's scrollback.
        var sane = TuiTerminalRepair.Sane;

        Assert.EndsWith("\x1b[?1049l", sane, StringComparison.Ordinal);
    }

    [Fact]
    public void Asking_for_a_repair_always_gets_one()
    {
        // `tui reset` is what a reader runs *because* they can see their terminal is wrong.
        // Making them convince it first would be a poor joke.
        var written = new List<string>();

        TuiTerminalRepair.Repair(written.Add);

        Assert.Equal([TuiTerminalRepair.Sane], written);
    }

    /// <summary>
    /// A terminal of this test's own, so the note-keeping can be exercised in a process
    /// whose output is a pipe — and so one test never repairs another's terminal.
    /// </summary>
    private static IDisposable Pretending([System.Runtime.CompilerServices.CallerMemberName] string name = "")
        => new Pretend($"/dev/pts/test-{name}");

    [Fact]
    public void A_terminal_nothing_says_is_broken_is_left_alone()
    {
        using var terminal = Pretending();

        // The property the whole design rests on: a shell must not write escapes at every
        // start on the chance that something is broken.
        var written = new List<string>();

        var repaired = TuiTerminalRepair.RepairIfNeeded(written.Add);

        Assert.False(repaired);
        Assert.Empty(written);
    }

    [Fact]
    public void A_session_that_never_gave_the_terminal_back_is_repaired_once()
    {
        using var terminal = Pretending();

        var written = new List<string>();

        TuiTerminalRepair.Taken();

        Assert.True(TuiTerminalRepair.RepairIfNeeded(written.Add));
        Assert.Equal([TuiTerminalRepair.Sane], written);

        // And only once: the note is gone, so the next shell on this terminal says nothing.
        written.Clear();

        Assert.False(TuiTerminalRepair.RepairIfNeeded(written.Add));
        Assert.Empty(written);
    }

    [Fact]
    public void A_session_that_ended_properly_leaves_nothing_to_repair()
    {
        using var terminal = Pretending();

        TuiTerminalRepair.Taken();
        TuiTerminalRepair.Given();

        Assert.False(TuiTerminalRepair.RepairIfNeeded(_ => { }));
    }

    [Fact]
    public void Pictures_are_only_taken_back_from_a_terminal_that_was_shown_any()
    {
        // An APC string is ignored by terminals that do not know it, but sending one to a
        // terminal that was never shown a picture is still saying something for no reason.
        var was = TuiGraphics.Protocol;

        try
        {
            TuiGraphics.Protocol = TuiGraphicsProtocol.HalfBlocks;

            var host = new Recording();

            using (TuiTerminalSession.Enter(host))
            {
            }

            Assert.DoesNotContain("_Ga=d", string.Concat(host.Written), StringComparison.Ordinal);

            TuiGraphics.Protocol = TuiGraphicsProtocol.Kitty;

            var speaking = new Recording();

            using (TuiTerminalSession.Enter(speaking))
            {
            }

            Assert.Contains("_Ga=d,d=A", string.Concat(speaking.Written), StringComparison.Ordinal);
        }
        finally
        {
            TuiGraphics.Protocol = was;
        }
    }

    [Fact]
    public void A_signal_says_so_as_well_as_restoring()
    {
        // Restoring on its own was not enough, and failed worse than not restoring: the
        // terminal came back and the process did not stop, so the render loop went on
        // drawing frames over the shell it had just handed back until the terminal driver
        // stopped it for reading in the background, leaving a suspended job behind.
        var host = new Recording();

        using var session = TuiTerminalSession.Enter(host);

        Assert.False(session.WasCancelled);

        session.Cancel();

        Assert.True(session.WasCancelled);
        Assert.Contains("\x1b[?1049l", string.Concat(host.Written), StringComparison.Ordinal);
    }

    [Fact]
    public void Ending_normally_is_not_a_cancellation()
    {
        // The loop reads this to decide whether to throw, so a screen that ended because
        // its own handler said so must not look like one the reader interrupted.
        var host = new Recording();
        var session = TuiTerminalSession.Enter(host);

        session.Dispose();

        Assert.False(session.WasCancelled);
    }

    /// <summary>Puts a terminal name in place for one test, and takes it away after.</summary>
    private sealed class Pretend : IDisposable
    {
        private readonly Func<string?> _was = TuiTerminalRepair.Terminal;

        public Pretend(string name) => TuiTerminalRepair.Terminal = () => name;

        public void Dispose()
        {
            TuiTerminalRepair.Given();
            TuiTerminalRepair.Terminal = _was;
        }
    }

    private sealed class Recording : ITuiHost
    {
        public List<string> Written { get; } = [];

        public bool IsInteractive => true;

        public TuiSize? TryGetSize() => new(80, 24);

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

        public void Write(string text) => Written.Add(text);
    }
}
