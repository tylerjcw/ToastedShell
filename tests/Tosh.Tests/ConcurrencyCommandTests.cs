using System.Diagnostics;
using System.Threading.Channels;
using Tosh.Runtime;
using Tosh.Language;
using Tosh.Stdlib.Concurrency;

namespace Tosh.Tests;

public sealed class ConcurrencyCommandTests
{
    [Fact]
    public async Task Race_returns_first_settled_value()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            race { echo first } { echo second }
            """);

        Assert.Single(results);
        Assert.Equal("first", results[0]);
    }

    [Fact]
    public async Task Race_throws_when_first_completion_fails()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await engine.ExecuteToListAsync(
                """
                race { throw boom } { sleep 0.05; echo ok }
                """);
        });
    }

    [Fact]
    public async Task Race_is_truly_concurrent_not_sequential()
    {
        // If the two arms execute concurrently each 150 ms sleep overlaps.
        // Sequential execution would take ≥ 300 ms; concurrent takes ~150 ms.
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var sw = Stopwatch.StartNew();

        var results = await engine.ExecuteToListAsync(
            """
            race { sleep 0.15; echo A } { sleep 0.15; echo B }
            """);

        sw.Stop();
        Assert.Single(results);
        // Must complete well under 300 ms to prove concurrency.
        Assert.True(sw.ElapsedMilliseconds < 270,
            $"Expected concurrent race (~150 ms) but took {sw.ElapsedMilliseconds} ms (suggests sequential execution).");
    }

    [Fact]
    public async Task Race_faster_arm_wins()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            race { sleep 0.2; echo slow } { sleep 0.01; echo fast }
            """);

        Assert.Single(results);
        Assert.Equal("fast", results[0]);
    }

    [Fact]
    public async Task Race_isolated_scopes_do_not_share_mutations()
    {
        // Each arm declares its own local var — should not collide.
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            race { var x = "from-A"; sleep 0.01; echo $x } { var x = "from-B"; echo $x }
            """);

        // Winner is B (no sleep); its x should be "from-B".
        Assert.Single(results);
        Assert.Equal("from-B", results[0]);
    }

    [Fact]
    public async Task Settle_returns_fulfilled_and_rejected_outcomes()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            settle { echo ok } { throw boom }
            """);

        Assert.Equal(2, results.Count);

        var first = Assert.IsType<Dictionary<string, object?>>(results[0]);
        var second = Assert.IsType<Dictionary<string, object?>>(results[1]);

        Assert.Equal(0, Assert.IsType<int>(first["Index"]));
        Assert.Equal("fulfilled", Assert.IsType<string>(first["Status"]));
        Assert.Equal("ok", first["Value"]);
        Assert.Null(first["Error"]);

        Assert.Equal(1, Assert.IsType<int>(second["Index"]));
        Assert.Equal("rejected", Assert.IsType<string>(second["Status"]));
        Assert.Null(second["Value"]);
        Assert.Contains("boom", Assert.IsType<string>(second["Error"]), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Settle_is_truly_concurrent_not_sequential()
    {
        // Two 150 ms arms must complete concurrently in ~150 ms, not ~300 ms.
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var sw = Stopwatch.StartNew();

        var results = await engine.ExecuteToListAsync(
            """
            settle { sleep 0.15; echo A } { sleep 0.15; echo B }
            """);

        sw.Stop();
        Assert.Equal(2, results.Count);
        Assert.True(sw.ElapsedMilliseconds < 270,
            $"Expected concurrent settle (~150 ms) but took {sw.ElapsedMilliseconds} ms (suggests sequential execution).");
    }

    [Fact]
    public async Task Parallel_is_truly_concurrent_not_sequential()
    {
        // Four items each sleeping 150 ms — concurrent execution finishes in ~150 ms,
        // sequential would take ~600 ms.
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var sw = Stopwatch.StartNew();

        var results = await engine.ExecuteToListAsync(
            """
            echo 1 2 3 4 | parallel { sleep 0.15; echo $_ }
            """);

        sw.Stop();
        Assert.Equal(4, results.Count);
        // Generous upper bound: even with overhead this should be well under 4×150 ms.
        Assert.True(sw.ElapsedMilliseconds < 450,
            $"Expected concurrent parallel (~150 ms) but took {sw.ElapsedMilliseconds} ms (suggests sequential execution).");
    }

    [Fact]
    public async Task Timeout_throws_when_operation_exceeds_duration()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await Assert.ThrowsAsync<ToshDiagnosticException>(async () =>
        {
            await engine.ExecuteToListAsync(
                """
                timeout 0.05 { sleep 0.2; echo too-late }
                """);
        });
    }

    [Fact]
    public async Task Timeout_replays_outputs_when_operation_finishes_in_time()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            timeout 0.5 { sleep 0.01; echo on-time }
            """);

        Assert.Single(results);
        Assert.Equal("on-time", results[0]);
    }

    [Fact]
    public async Task Async_and_await_round_trip_outputs()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            var f = async { sleep 0.01; echo first; echo second }
            await $f
            """);

        Assert.Equal(2, results.Count);
        Assert.Equal("first", results[0]);
        Assert.Equal("second", results[1]);
    }

    [Fact]
    public async Task Channel_select_returns_first_available_value()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            var ch1 = channel
            var ch2 = channel
            var slow = async { sleep 0.1; channel-send $ch1 slow }
            var fast = async { sleep 0.01; channel-send $ch2 fast }
            var picked = channel-select $ch1 $ch2
            $picked | get Value
            """);

        Assert.Single(results);
        Assert.Equal("fast", results[0]);
    }

    [Fact]
    public async Task Channel_select_preserves_non_winning_buffered_item()
    {
        var first = ShellChannel.CreateUnbounded();
        var second = ShellChannel.CreateUnbounded();
        await first.SendAsync("first");
        await second.SendAsync("second");
        first.Close();
        second.Close();

        var context = new CommandContext(
            ToshRuntime.CreateDefault(),
            AsyncEnumerableExtensions.Empty<object?>(),
            [first, second],
            CancellationToken.None);

        var results = await Tosh.Runtime.AsyncEnumerableExtensions.ToListAsync(
            new ChannelSelectCommand().ExecuteAsync(context));

        var selected = Assert.IsType<Dictionary<string, object?>>(Assert.Single(results));
        var selectedIndex = Assert.IsType<int>(selected["Index"]);
        Assert.InRange(selectedIndex, 0, 1);

        var losingChannel = selectedIndex == 0 ? second : first;
        Assert.True(losingChannel.TryReceive(out var losingValue));

        Assert.Equal(selectedIndex == 0 ? "first" : "second", selected["Value"]);
        Assert.Equal(selectedIndex == 0 ? "second" : "first", losingValue);
    }

    [Fact]
    public async Task Channel_select_accepts_null_payload()
    {
        var withNull = ShellChannel.CreateUnbounded();
        var closed = ShellChannel.CreateUnbounded();
        await withNull.SendAsync(null);
        withNull.Close();
        closed.Close();

        var context = new CommandContext(
            ToshRuntime.CreateDefault(),
            AsyncEnumerableExtensions.Empty<object?>(),
            [withNull, closed],
            CancellationToken.None);

        var results = await Tosh.Runtime.AsyncEnumerableExtensions.ToListAsync(
            new ChannelSelectCommand().ExecuteAsync(context));

        var selected = Assert.IsType<Dictionary<string, object?>>(Assert.Single(results));
        Assert.Equal(0, Assert.IsType<int>(selected["Index"]));
        Assert.True(selected.ContainsKey("Value"));
        Assert.Null(selected["Value"]);
    }

    [Fact]
    public async Task Channel_select_cancellation_leaves_channels_usable()
    {
        var first = ShellChannel.CreateUnbounded();
        var second = ShellChannel.CreateUnbounded();
        using var cancellation = new CancellationTokenSource();
        var context = new CommandContext(
            ToshRuntime.CreateDefault(),
            AsyncEnumerableExtensions.Empty<object?>(),
            [first, second],
            cancellation.Token);

        var selection = Tosh.Runtime.AsyncEnumerableExtensions.ToListAsync(
            new ChannelSelectCommand().ExecuteAsync(context));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => selection.WaitAsync(TimeSpan.FromSeconds(2)));

        await first.SendAsync("first");
        await second.SendAsync("second");
        Assert.True(first.TryReceive(out var firstValue));
        Assert.True(second.TryReceive(out var secondValue));
        Assert.Equal("first", firstValue);
        Assert.Equal("second", secondValue);

        first.Close();
        second.Close();
    }

    [Fact]
    public async Task Channel_single_receive_distinguishes_null_from_closed_and_drained()
    {
        var channel = ShellChannel.CreateUnbounded();
        await channel.SendAsync(null);
        channel.Close();

        var value = await channel.ReceiveResultAsync();
        var completed = await channel.ReceiveResultAsync();

        Assert.True(value.HasValue);
        Assert.Null(value.Value);
        Assert.False(completed.HasValue);
        Assert.Null(completed.Value);
    }

    [Fact]
    public async Task Legacy_channel_receive_returns_null_payload_and_throws_at_completion()
    {
        var channel = ShellChannel.CreateUnbounded();
        await channel.SendAsync(null);
        channel.Close();

        Assert.Null(await channel.ReceiveAsync());
        await Assert.ThrowsAsync<ChannelClosedException>(
            () => channel.ReceiveAsync().AsTask());
    }

    [Fact]
    public async Task Concurrent_single_receivers_do_not_report_spurious_completion()
    {
        var channel = ShellChannel.CreateUnbounded();
        var receivers = Enumerable
            .Range(0, 64)
            .Select(_ => channel.ReceiveResultAsync().AsTask())
            .ToArray();

        await Task.Delay(20);
        await channel.SendAsync("only-item");

        var firstCompleted = await Task
            .WhenAny(receivers)
            .WaitAsync(TimeSpan.FromSeconds(2));
        var firstResult = await firstCompleted;

        Assert.True(firstResult.HasValue);
        Assert.Equal("only-item", firstResult.Value);

        await Task.Delay(50);
        Assert.Equal(1, receivers.Count(static task => task.IsCompleted));

        channel.Close();
        var results = await Task
            .WhenAll(receivers)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Single(results, static result => result.HasValue);
        Assert.Equal(63, results.Count(static result => !result.HasValue));
    }

    [Fact]
    public async Task Concurrent_legacy_receivers_wait_for_distinct_values()
    {
        var channel = ShellChannel.CreateUnbounded();
        var first = channel.ReceiveAsync().AsTask();
        var second = channel.ReceiveAsync().AsTask();

        await Task.Delay(20);
        await channel.SendAsync("first");

        var winner = await Task
            .WhenAny(first, second)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("first", await winner);

        var remaining = ReferenceEquals(winner, first) ? second : first;
        await Task.Delay(50);
        Assert.False(remaining.IsCompleted);

        await channel.SendAsync("second");
        Assert.Equal(
            "second",
            await remaining.WaitAsync(TimeSpan.FromSeconds(2)));
        channel.Close();
    }

    [Fact]
    public async Task Cancelled_single_receive_leaves_the_channel_usable()
    {
        var channel = ShellChannel.CreateUnbounded();
        using var cancellation = new CancellationTokenSource();
        var pending = channel.ReceiveResultAsync(cancellation.Token).AsTask();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pending.WaitAsync(TimeSpan.FromSeconds(2)));

        await channel.SendAsync("after-cancellation");
        var result = await channel.ReceiveResultAsync();

        Assert.True(result.HasValue);
        Assert.Equal("after-cancellation", result.Value);
        channel.Close();
    }

    [Fact]
    public async Task Channel_recv_streams_null_payload_but_no_value_for_completion()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            var ch = channel
            channel-send $ch null
            channel-close $ch
            channel-recv $ch
            """);

        Assert.Single(results);
        Assert.Null(results[0]);
    }
}
