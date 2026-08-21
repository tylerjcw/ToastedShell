using System.Diagnostics;
using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A user-defined function streams its output — <c>TS-P1-07</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>ExecuteFunctionAsync</c> had two branches, and the second was labelled
/// "Non-generator functions buffer all output before yielding". So a function was the
/// only thing in a pipeline that could not be short-circuited: measured before the fix,
/// <c>gen | first</c> over an unbounded producer ran the loop forever — 800,000 values in
/// ten seconds, never terminating — while <c>seq 1 20000000 | first</c> and
/// <c>yes | first</c> both returned in 0.25s.
/// </para>
/// <para>
/// The buffering worked around a C# restriction rather than expressing a semantic:
/// <c>return</c> raises a signal that must be caught around the whole enumeration, and
/// <c>yield return</c> is not permitted inside a try-with-catch. The generator branch had
/// already solved that with a manual enumerator, so both branches now share it and the
/// whole suite passes unchanged — which is the evidence that the buffering was
/// incidental.
/// </para>
/// <para>
/// <b>Not yet streaming, and deliberately left:</b> a single command statement inside a
/// block is still drained, because <c>UpdateLastResultIfAny</c> needs every value to set
/// <c>$tosh.Last.Result</c> for a multi-value statement. <c>func g() { yes } | first</c>
/// therefore still runs forever. That is a decision about <c>$tosh.Last</c>, not a
/// workaround, and it is filed rather than guessed at.
/// </para>
/// </remarks>
public sealed class FunctionStreamingTests
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
    public async Task A_consumer_can_short_circuit_a_function()
    {
        // The decisive case: the loop is unbounded, so this can only terminate if `first`
        // stops the producer. Before the fix it ran until killed.
        var results = await RunAsync(
            """
            func gen() {
                var i = 0
                while (true) {
                    $i = ($i + 1)
                    $i
                }
            }
            gen | first
            """,
            TimeSpan.FromSeconds(20));

        Assert.Equal(1, Convert.ToInt32(Assert.Single(results)));
    }

    [Fact]
    public async Task A_function_produces_only_what_is_consumed()
    {
        // Counts the producer's side effects rather than timing anything, so it states
        // the property directly and cannot flake under load.
        var results = await RunAsync(
            """
            var produced = 0
            func gen() {
                for i in 1..8 {
                    $produced = ($produced + 1)
                    $i
                }
            }
            gen | first | ignore
            $produced
            """);

        Assert.Equal(1, Convert.ToInt32(Assert.Single(results)));
    }

    [Fact]
    public async Task Values_interleave_with_the_consumer()
    {
        // Buffering and streaming give the same *values*; only the order of the side
        // effects tells them apart. Buffered: produce×3 then consume×3.
        var results = await RunAsync(
            """
            var log = []
            func gen() {
                for i in 1..3 {
                    $log = [...$log, $"p{$i}"]
                    $i
                }
            }
            gen | each { $log = [...$log, $"c{$_}"] } | ignore
            $log | join -s ","
            """);

        Assert.Equal("p1,c1,p2,c2,p3,c3", Assert.Single(results)?.ToString());
    }

    // ── Semantics that must survive streaming ──────────────────────────────────

    [Fact]
    public async Task Values_emitted_before_a_return_still_come_first()
    {
        // The item's own wording: previously emitted values stream unchanged, and the
        // optional return value is final.
        var results = await RunAsync(
            """
            func gen() {
                1
                2
                return 99
            }
            gen
            """);

        Assert.Equal([1, 2, 99], results.Select(v => Convert.ToInt32(v)));
    }

    [Fact]
    public async Task A_plain_function_still_returns_its_value()
    {
        var results = await RunAsync("func f() { return 7 }\nf");

        Assert.Equal(7, Convert.ToInt32(Assert.Single(results)));
    }

    [Fact]
    public async Task A_generator_is_unchanged()
    {
        var results = await RunAsync(
            """
            func gen() {
                yield 1
                yield 2
            }
            gen | collect | count
            """);

        // `TOAST-0028`. `collect` exists to gather a stream into one collection, so
        // counting its output is 1 — the same answer `cast list<int> | count` gives, and
        // for the same reason. Counting the *items* is `gen | count`, which is 2 and needs
        // no intermediary. This read 2 until 2026-08-21, because the collection arrived
        // alone and the consumer undid the collecting.
        Assert.Equal(1, Convert.ToInt32(Assert.Single(results)));

        var spread = await RunAsync(
            """
            func gen() {
                yield 1
                yield 2
            }
            echo ...(gen | collect) | count
            """);

        Assert.Equal(2, Convert.ToInt32(Assert.Single(spread)));
    }

    [Fact]
    public async Task Break_outside_a_loop_is_still_a_diagnostic()
    {
        // The signal handling the manual enumerator has to preserve.
        var error = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => RunAsync("func f() { break }\nf"));

        Assert.Contains(error.Diagnostics, d => d.Code == "tosh.runtime.break_outside_loop");
    }

    [Fact]
    public async Task A_function_that_throws_still_throws()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(
            """
            func f() { throw "boom" }
            f
            """));
    }

    [Fact]
    public async Task Defer_still_runs_after_the_body()
    {
        // TS-P1-07's first half. The defer path buffers on purpose — cleanup has to run
        // after the body — and that must not have been disturbed.
        var results = await RunAsync(
            """
            var log = []
            func f() {
                defer { $log = [...$log, "cleanup"] }
                $log = [...$log, "body"]
            }
            f | ignore
            $log | join -s ","
            """);

        Assert.Equal("body,cleanup", Assert.Single(results)?.ToString());
    }
}
