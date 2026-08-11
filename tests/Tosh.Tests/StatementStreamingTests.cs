using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A statement inside a block streams, and <c>$tosh.Last.Result</c> survives it —
/// <c>TS-P1-45</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>ExecuteBlockStatementsAsync</c> drained every statement that was not a
/// <c>yield</c>, so a bare command inside a block was materialized whole even after
/// <c>TS-P1-07</c> made the function boundary itself lazy:
/// <c>func g() { yes } | first</c> never terminated, and
/// <c>func g() { seq 1 N } | first</c> scaled linearly — 1.45s at five million, 4.86s at
/// twenty — while the same loop as <c>for i in 1..N { $i }</c> short-circuited in 0.28s.
/// Both are now 0.27s.
/// </para>
/// <para>
/// Two things depended on the drain and only one is a real constraint. Suppression is
/// not: it fires only for a single-stage expression pipeline whose values are all null,
/// so it cannot apply to the statements streamed here. <c>$tosh.Last.Result</c> is: it
/// holds the whole array when a statement produced several, which cannot be known without
/// keeping them. Values are therefore retained up to a budget and the last result is set
/// from them; past the budget it is *cleared* rather than left stale, since reading a
/// previous statement's output silently is worse than reading nothing.
/// </para>
/// </remarks>
public sealed class StatementStreamingTests
{
    private static async Task<IReadOnlyList<object?>> RunAsync(string script, TimeSpan? budget = null)
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);
        using var cts = new CancellationTokenSource(budget ?? TimeSpan.FromSeconds(30));

        var results = new List<object?>();

        await foreach (var value in engine.EvaluateAsync(script, "<probe>", cts.Token))
        {
            results.Add(value);
        }

        return results;
    }

    [Fact]
    public async Task A_bare_command_statement_in_a_block_can_be_short_circuited()
    {
        // Deliberately unbounded. A large-but-finite producer makes a weak control: the
        // first version of this used `seq 1 20000000`, which the *drained* engine also
        // finished — in 4.9s, inside the budget — so it passed either way and proved
        // nothing. `yes` never ends, so this can only terminate if `first` stops the
        // producing statement. It is the exact case the item describes.
        var results = await RunAsync(
            """
            func g() { yes }
            g | first
            """,
            TimeSpan.FromSeconds(20));

        Assert.Equal("y", Assert.Single(results)?.ToString());
    }

    [Fact]
    public async Task A_second_statement_does_not_run_once_the_consumer_stops()
    {
        // A regression guard rather than a control: it passes against the drained engine
        // too, because `TS-P1-07` had already made the *function* stream, so abandoning
        // the enumeration stopped the block either way. It is kept because the property
        // has to keep holding, not because it distinguishes this change — the two tests
        // that do are the unbounded short-circuit and the retention budget.
        var results = await RunAsync(
            """
            var reached = false
            func g() {
                seq 1 100
                $reached = true
            }
            g | first | ignore
            $reached
            """);

        Assert.False(Convert.ToBoolean(Assert.Single(results)));
    }

    // ── `$tosh.Last.Result`, the reason this needed a decision ─────────────────

    [Fact]
    public async Task An_ordinary_statement_still_sets_the_whole_result()
    {
        // Under the budget nothing observable changes, which is the point of having one.
        var results = await RunAsync(
            """
            func f() {
                seq 1 3
                var snap = $tosh.Last.Result
                $snap.Length
            }
            f | last
            """);

        Assert.Equal(3, Convert.ToInt32(Assert.Single(results)));
    }

    [Fact]
    public async Task A_single_value_statement_still_sets_that_value()
    {
        var results = await RunAsync(
            """
            func f() {
                seq 7 7
                var snap = $tosh.Last.Result
                $snap
            }
            f | last
            """);

        Assert.Equal(7, Convert.ToInt64(Assert.Single(results)));
    }

    [Fact]
    public async Task Past_the_budget_the_last_result_is_cleared_not_stale()
    {
        // The half that matters most. Leaving a *previous* statement's output in place
        // would be silently wrong; null says "not available" and can be tested for.
        var results = await RunAsync(
            """
            func f() {
                seq 1 3
                seq 1 50000 | ignore
                seq 1 50000
                var snap = $tosh.Last.Result
                ($snap == null)
            }
            f | last
            """,
            TimeSpan.FromSeconds(60));

        Assert.True(Convert.ToBoolean(Assert.Single(results)));
    }

    // ── Nothing that already worked changed ────────────────────────────────────

    [Fact]
    public async Task Suppression_still_applies_where_it_did()
    {
        // A single-stage expression pipeline evaluating to null emits nothing. That is
        // the one shape still drained, because suppression needs every value before it
        // can decide to emit none — which is exactly why `CanStreamStatementResults`
        // excludes it.
        var results = await RunAsync(
            """
            var nothing = null
            func f() { $nothing }
            var c = (f | collect)
            $c.Length
            """);

        Assert.Equal(0, Convert.ToInt32(Assert.Single(results)));
    }

    [Fact]
    public async Task Statement_order_and_values_are_unchanged()
    {
        var results = await RunAsync(
            """
            func f() {
                1
                seq 2 3
                4
            }
            f
            """);

        Assert.Equal([1, 2, 3, 4], results.Select(v => Convert.ToInt64(v)));
    }

    [Fact]
    public async Task A_defer_block_still_runs_after_the_body()
    {
        var results = await RunAsync(
            """
            var log = []
            func f() {
                defer { $log = [...$log, "cleanup"] }
                seq 1 2 | ignore
                $log = [...$log, "body"]
            }
            f | ignore
            $log | join -s ","
            """);

        Assert.Equal("body,cleanup", Assert.Single(results)?.ToString());
    }
}
