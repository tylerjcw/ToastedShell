using System.Text;
using Tosh.Tui;
using Tosh.Tui.Rendering;

namespace Tosh.Tests;

/// <summary>
/// The guarantees that matter because TōSh is a login shell: the terminal comes back,
/// and a mistake in a script handler does not end the session.
/// </summary>
public sealed class TuiTerminalSafetyTests
{
    /// <summary>A host that records what was written to it and nothing else.</summary>
    private sealed class RecordingHost : ITuiHost
    {
        private readonly StringBuilder _written = new();

        public bool IsInteractive => true;

        public string Written => _written.ToString();

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

        public void Write(string text) => _written.Append(text);
    }

    [Fact]
    public void Entering_a_session_switches_the_terminal_into_full_screen()
    {
        var host = new RecordingHost();

        using var session = TuiTerminalSession.Enter(host);

        Assert.Contains("[?1049h", host.Written, StringComparison.Ordinal);
        Assert.Contains("[?25l", host.Written, StringComparison.Ordinal);
        Assert.Contains("[?1000h", host.Written, StringComparison.Ordinal);
    }

    [Fact]
    public void Restoring_puts_back_everything_it_switched_on()
    {
        var host = new RecordingHost();
        var session = TuiTerminalSession.Enter(host);

        session.Restore();

        Assert.Contains("[?1000l", host.Written, StringComparison.Ordinal);
        Assert.Contains("[?25h", host.Written, StringComparison.Ordinal);
        Assert.Contains("[?1049l", host.Written, StringComparison.Ordinal);

        // Styling too: a screen that exits mid-colour should not tint the next prompt.
        Assert.Contains("[0m", host.Written, StringComparison.Ordinal);
    }

    [Fact]
    public void Restoring_twice_writes_the_escape_sequences_once()
    {
        var host = new RecordingHost();
        var session = TuiTerminalSession.Enter(host);

        session.Restore();
        session.Restore();
        session.Dispose();

        // A signal handler and the finally block can both arrive; only one should act.
        Assert.Equal(1, Occurrences(host.Written, "[?1049l"));
    }

    [Fact]
    public void A_handler_that_throws_does_not_end_the_session()
    {
        var reporter = new TuiHandlerFailureReporter();

        var result = reporter.Guard(() => throw new InvalidOperationException("no such property"));

        Assert.Equal(TuiScreenResult.Continue, result);
    }

    [Fact]
    public void A_handler_failure_is_drawn_where_the_user_will_see_it()
    {
        var reporter = new TuiHandlerFailureReporter();
        reporter.Guard(() => throw new InvalidOperationException("no such property"));

        // 80 columns, because the banner carries the exception type as well as the
        // message and a narrower terminal clips the tail of it.
        var buffer = new TuiBuffer(new TuiSize(80, 4));
        reporter.Draw(buffer);

        var banner = buffer.RowText(3);
        Assert.Contains("handler failed", banner, StringComparison.Ordinal);
        Assert.Contains("no such property", banner, StringComparison.Ordinal);
        Assert.Equal(new string(' ', 80), buffer.RowText(0));
    }

    [Fact]
    public void The_same_failure_every_tick_is_counted_rather_than_repeated()
    {
        var reporter = new TuiHandlerFailureReporter();

        for (var tick = 0; tick < 5; tick += 1)
        {
            reporter.Guard(() => throw new InvalidOperationException("still broken"));
        }

        var buffer = new TuiBuffer(new TuiSize(60, 1));
        reporter.Draw(buffer);

        Assert.Contains("(x5)", buffer.RowText(0), StringComparison.Ordinal);
    }

    [Fact]
    public void A_different_failure_replaces_the_previous_one()
    {
        var reporter = new TuiHandlerFailureReporter();
        reporter.Guard(() => throw new InvalidOperationException("first"));
        reporter.Guard(() => throw new InvalidOperationException("second"));

        var buffer = new TuiBuffer(new TuiSize(60, 1));
        reporter.Draw(buffer);

        Assert.Contains("second", buffer.RowText(0), StringComparison.Ordinal);
        Assert.DoesNotContain("first", buffer.RowText(0), StringComparison.Ordinal);
    }

    [Fact]
    public void A_screen_with_no_failures_draws_no_banner()
    {
        var reporter = new TuiHandlerFailureReporter();
        var buffer = new TuiBuffer(new TuiSize(10, 2));
        buffer.DrawText(0, 1, "content", TuiStyle.Default);

        reporter.Draw(buffer);

        Assert.Equal("content   ", buffer.RowText(1));
    }

    private static int Occurrences(string text, string value)
        => text.Split(value).Length - 1;
}
