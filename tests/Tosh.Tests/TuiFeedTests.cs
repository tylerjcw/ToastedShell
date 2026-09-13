using Tosh.Runtime;
using Tosh.Tui;
using Tosh.Tui.Declarative;
using Tosh.Tui.Rendering;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

/// <summary>
/// A screen fed by something other than the clock (<c>TUI-0008</c>).
/// </summary>
public sealed class TuiFeedTests
{
    private static bool Settle(Func<bool> until, int milliseconds = 2000)
    {
        var deadline = Environment.TickCount64 + milliseconds;

        while (Environment.TickCount64 < deadline)
        {
            if (until())
            {
                return true;
            }

            Thread.Sleep(5);
        }

        return until();
    }

    [Fact]
    public void Posted_work_runs_on_the_thread_that_drains_it()
    {
        var wake = new TuiWake();
        var ran = 0;

        Assert.False(wake.TryTake());

        wake.Post(() => ran += 1);

        Assert.True(wake.TryTake());

        wake.Drain();

        Assert.Equal(1, ran);
    }

    [Fact]
    public void A_thousand_arrivals_ask_for_one_frame()
    {
        // The coalescing that keeps a fast source from redrawing per value: the signal is
        // a flag, not a count, so however many arrive between frames there is one frame.
        var wake = new TuiWake();

        for (var index = 0; index < 1000; index += 1)
        {
            wake.Signal();
        }

        Assert.True(wake.TryTake());
        Assert.False(wake.TryTake());
    }

    [Fact]
    public void A_drain_that_runs_long_leaves_the_signal_up()
    {
        // A source producing faster than the terminal can draw would otherwise hold the
        // loop in the drain and the screen would never repaint.
        var wake = new TuiWake();
        var ran = 0;

        for (var index = 0; index < TuiWake.DrainLimit + 5; index += 1)
        {
            wake.Post(() => ran += 1);
        }

        Assert.True(wake.TryTake());

        wake.Drain();

        Assert.Equal(TuiWake.DrainLimit, ran);
        Assert.True(wake.TryTake());

        wake.Drain();

        Assert.Equal(TuiWake.DrainLimit + 5, ran);
    }

    [Fact]
    public async Task A_channel_feeds_a_widget_without_an_interval()
    {
        var channel = ShellChannel.CreateUnbounded();
        var lines = new List<string>();

        var text = new TuiTextWidget("waiting");
        var feeds = new TuiFeeds();

        feeds.From(channel, value =>
        {
            lines.Add(value?.ToString() ?? string.Empty);
            text.Text = string.Join(", ", lines);
        });

        text.Feeds = feeds;

        using var screen = new TuiDeclarativeScreen(text, []);

        Assert.NotNull(screen.Wake);
        Assert.Null(screen.RefreshInterval);

        await channel.SendAsync("one");
        await channel.SendAsync("two");

        Assert.True(Settle(() => screen.Wake!.TryTake()), "The loop was never woken.");

        screen.Wake!.Drain();

        Assert.True(Settle(() =>
        {
            screen.Wake!.TryTake();
            screen.Wake!.Drain();
            return lines.Count == 2;
        }));

        Assert.Equal(["one", "two"], lines);
        Assert.Equal("one, two", screen.Render(new TuiSize(20, 1)).ToPlainText());
    }

    [Fact]
    public void A_source_that_runs_out_says_so()
    {
        var ended = false;
        var seen = new List<object?>();

        var text = new TuiTextWidget("x");
        var feeds = new TuiFeeds();

        feeds.From(new object?[] { 1, 2, 3 }, seen.Add, () => ended = true);
        text.Feeds = feeds;

        using var screen = new TuiDeclarativeScreen(text, []);

        Assert.True(Settle(() =>
        {
            screen.Wake!.TryTake();
            screen.Wake!.Drain();
            return ended;
        }));

        Assert.Equal([1, 2, 3], seen);
    }

    [Fact]
    public void A_screen_with_no_sources_is_not_woken_at_all()
    {
        // The property the whole design rests on: a screen that only changes when the
        // reader does something still blocks on the keyboard rather than waiting in slices.
        using var screen = new TuiDeclarativeScreen(new TuiTextWidget("static"), []);

        Assert.Null(screen.Wake);
    }

    [Fact]
    public void A_source_that_fails_reports_on_the_loops_thread()
    {
        var text = new TuiTextWidget("x");
        var feeds = new TuiFeeds();

        feeds.From(Failing(), _ => { });
        text.Feeds = feeds;

        using var screen = new TuiDeclarativeScreen(text, []);

        Assert.True(Settle(() => screen.Wake!.TryTake()));

        // Thrown on the drain, which the run loop wraps in the same guard as every other
        // handler — so it reaches the banner rather than a task nobody awaits.
        Assert.Throws<TuiFeedException>(screen.Wake!.Drain);

        static async IAsyncEnumerable<object?> Failing()
        {
            await Task.Yield();
            throw new InvalidOperationException("the pipe broke");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }
}
