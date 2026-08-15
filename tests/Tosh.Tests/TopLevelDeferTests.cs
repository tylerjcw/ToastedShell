using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A `defer` written at the top of a script runs when the script scope exits.
///
/// `TS-P2-89`. It used to dispatch to an empty sequence: it parsed, bound,
/// reported nothing, and never ran. The defer machinery lived entirely in
/// `ExecuteBlockAsync`, and the top level runs its own statement loop rather than
/// going through it — so the one scope a script actually starts in was the one
/// scope without cleanup, and every resource-owning script had to wrap its body
/// in a function it did not otherwise need.
///
/// Buffering is the cost and the reason the path is gated: cleanup must run
/// before values are handed on, so a script using `defer` cannot stream. A script
/// that does not use it is untouched, which is the same trade `ExecuteBlockAsync`
/// already makes one scope down.
/// </summary>
public class TopLevelDeferTests
{
    private static async Task<string> RunAsync(string source)
    {
        var output = new StringWriter();
        var engine = new ToshEngine(ToshRuntime.CreateDefault(output, output));
        await engine.ExecuteToListAsync(source);
        return output.ToString().Replace("\r", "").Trim();
    }

    [Fact]
    public async Task A_top_level_defer_runs_when_the_script_ends()
        => Assert.Equal("body\ncleanup", await RunAsync(
            """
            defer { writeline "cleanup" }
            writeline "body"
            """));

    /// <summary>
    /// Last registered runs first, as in every other scope. Asserting only that
    /// "cleanup happened" would pass on a fix that ran them in registration order.
    /// </summary>
    [Fact]
    public async Task Top_level_defers_run_last_registered_first()
        => Assert.Equal("body\nB\nA", await RunAsync(
            """
            defer { writeline "A" }
            defer { writeline "B" }
            writeline "body"
            """));

    /// <summary>
    /// A failing body still runs cleanup, and still reports the failure — the case
    /// `defer` exists for.
    /// </summary>
    [Fact]
    public async Task Cleanup_runs_when_the_body_throws_and_the_failure_still_surfaces()
    {
        var output = new StringWriter();
        var engine = new ToshEngine(ToshRuntime.CreateDefault(output, output));

        await Assert.ThrowsAnyAsync<Exception>(() => engine.ExecuteToListAsync(
            """
            defer { writeline "cleanup" }
            writeline "before"
            throw "boom"
            """));

        Assert.Contains("cleanup", output.ToString());
    }

    /// <summary>
    /// Values produced before the end are still emitted; buffering changes when
    /// they appear, not whether they do.
    /// </summary>
    [Fact]
    public async Task Values_from_a_script_with_a_defer_are_still_produced()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(
            """
            defer { }
            1
            2
            """);

        Assert.Equal(["1", "2"], results.Select(v => v?.ToString()));
    }

    /// <summary>
    /// A script without `defer` keeps the streaming path untouched — the gate is
    /// the reason the buffering cost is not paid by every script.
    /// </summary>
    [Fact]
    public async Task A_script_without_a_defer_is_unaffected()
        => Assert.Equal("a\nb", await RunAsync(
            """
            writeline "a"
            writeline "b"
            """));

    /// <summary>
    /// `exit` does not run deferred blocks — and that is pinned rather than fixed,
    /// because it is **not** specific to the top level: a function-scope `defer`
    /// behaves identically, measured before this change. Cleanup is emitted after
    /// the buffered values, so when the consumer stops pulling on exit it is never
    /// reached, at any scope. Making the top level differ from a block here would
    /// have traded one inconsistency for another; the shared behaviour is recorded
    /// separately.
    /// </summary>
    [Fact]
    public async Task Exit_skips_cleanup_at_the_top_level_exactly_as_it_does_in_a_function()
    {
        var topLevel = new StringWriter();
        var topLevelEngine = new ToshEngine(ToshRuntime.CreateDefault(topLevel, topLevel));
        await topLevelEngine.ExecuteToListAsync(
            """
            defer { writeline "cleanup" }
            writeline "before"
            exit 0
            """);

        var inFunction = new StringWriter();
        var functionEngine = new ToshEngine(ToshRuntime.CreateDefault(inFunction, inFunction));
        await functionEngine.ExecuteToListAsync(
            """
            func work() {
                defer { writeline "cleanup" }
                writeline "before"
                exit 0
            }
            work
            """);

        Assert.DoesNotContain("cleanup", topLevel.ToString());
        Assert.DoesNotContain("cleanup", inFunction.ToString());
    }
}
