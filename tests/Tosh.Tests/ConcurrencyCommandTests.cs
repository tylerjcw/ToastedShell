using System.Diagnostics;
using Tosh.Core;
using Tosh.Language;

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
}
