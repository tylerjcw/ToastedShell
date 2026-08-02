using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A generator streams its loop, so an endless one is usable — <c>TS-P1-19</c>.
/// </summary>
/// <remarks>
/// <para>
/// `gen | first 3` against an endless generator produced nothing and `gen() | first 3` hung. By
/// the time this was fixed the two forms behaved alike — the parser repair in <c>TS-P1-08</c> had
/// already removed the difference — and both hung. The "no output" half of the report was a
/// process killed before its buffer flushed, not an empty stream.
/// </para>
/// <para>
/// The cause was in <c>ExecuteBlockAsync</c>, which drained every statement with
/// <c>ToListAsync</c> before letting a single value out of the block. A bare <c>yield</c> escaped
/// that through its own branch, which is why `yield 1; yield 2` streamed perfectly while the same
/// yields wrapped in a loop did not: the loop is *one statement*, and materialising every value it
/// will ever produce does not finish. The loop evaluators were already lazy; nothing downstream
/// ever got the chance to ask them for one item.
/// </para>
/// <para>
/// Two further defects surfaced while confirming the first, both of which this file guards:
/// </para>
/// <list type="bullet">
/// <item>
/// <c>return</c> inside a loop discarded the values that iteration had already yielded, because
/// the signal escaped past the per-iteration buffer that holds them. <c>break</c> was always
/// right — it is stashed in a flag and the buffer is flushed first — so <c>return</c> now takes
/// the same route.
/// </item>
/// <item>
/// <c>until</c> was absent from the yield-detecting walk. A function whose only <c>yield</c> sat
/// in an <c>until</c> loop was not classified as a generator at all, and the loop was collected
/// rather than streamed.
/// </item>
/// </list>
/// <para>
/// Every case here runs under a cancellation deadline. The defect's signature is non-termination,
/// so a regression must fail the test rather than hang the suite behind it.
/// </para>
/// </remarks>
public sealed class InfiniteGeneratorTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Runs <paramref name="source"/> and joins the resulting values, failing rather than hanging
    /// if the engine does not finish.
    /// </summary>
    private static async Task<string> RunAsync(string source)
    {
        using var deadline = new CancellationTokenSource(Deadline);
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        IReadOnlyList<object?> results;
        try
        {
            results = await engine.ExecuteToListAsync(source, deadline.Token);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail(
                $"The script did not finish within {Deadline.TotalSeconds:0}s — the generator is " +
                "being collected instead of streamed.");
            throw;
        }

        return string.Join(",", results.Select(value => value?.ToString() ?? "null"));
    }

    /// <summary>
    /// How many items <paramref name="source"/> caused its generator to produce, counted through
    /// <c>writeline</c> because it writes directly rather than into the pipeline — values sent
    /// down the pipeline are consumed by the very command whose appetite is being measured.
    /// </summary>
    private static async Task<int> ProducedCountAsync(string source)
    {
        using var deadline = new CancellationTokenSource(Deadline);
        var writer = new StringWriter();
        var runtime = ToshRuntime.CreateDefault();
        runtime.Output = writer;

        try
        {
            await new ToshEngine(runtime).ExecuteToListAsync(source, deadline.Token);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail($"The script did not finish within {Deadline.TotalSeconds:0}s.");
            throw;
        }

        return writer.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.TrimEnd('\r') == "p");
    }

    // ── The report ─────────────────────────────────────────────────────────────

    private const string Endless =
        """
        func gen() {
            var i = 0
            while (true) {
                yield $i
                $i = ($i + 1)
            }
        }
        """;

    [Theory]
    // Command position is how it was reported; the call form is the spelling that hung. They are
    // the same path today and both are pinned so they cannot drift apart again.
    [InlineData("gen | first 3")]
    [InlineData("gen() | first 3")]
    public async Task An_endless_generator_serves_a_short_circuiting_consumer(string consumer)
    {
        Assert.Equal("0,1,2", await RunAsync($"{Endless}\n{consumer}"));
    }

    [Theory]
    // Both endless loop forms, since `until` reached the fix by a different route than `while`.
    [InlineData("while (true)")]
    [InlineData("until (false)")]
    public async Task Any_endless_loop_form_streams(string loop)
    {
        Assert.Equal("0,1,2", await RunAsync(
            $$"""
            func gen() {
                var i = 0
                {{loop}} {
                    yield $i
                    $i = ($i + 1)
                }
            }
            gen | first 3
            """));
    }

    // ── Laziness, measured rather than inferred ────────────────────────────────

    [Theory]
    [InlineData("while ($i < 50)")]
    [InlineData("until ($i >= 50)")]
    public async Task A_loop_produces_only_what_is_pulled(string loop)
    {
        // A bounded loop, so this fails by counting wrong rather than by hanging — the same
        // property as the endless case, observable when the endless one cannot be measured.
        Assert.Equal(1, await ProducedCountAsync(
            $$"""
            func gen() {
                var i = 0
                {{loop}} {
                    writeline "p"
                    yield $i
                    $i = ($i + 1)
                }
            }
            gen | first 1 | ignore
            """));
    }

    [Fact]
    public async Task A_for_loop_produces_only_what_is_pulled()
    {
        Assert.Equal(1, await ProducedCountAsync(
            """
            func gen() {
                for i in [1, 2, 3, 4, 5, 6, 7, 8, 9, 10] {
                    writeline "p"
                    yield $i
                }
            }
            gen | first 1 | ignore
            """));
    }

    [Fact]
    public async Task A_yield_outside_a_loop_is_still_lazy()
    {
        // The control: this spelling always streamed, and is what made the loop case stand out.
        Assert.Equal(1, await ProducedCountAsync(
            """
            func gen() {
                writeline "p"
                yield 1
                writeline "p"
                yield 2
                writeline "p"
                yield 3
            }
            gen | first 1 | ignore
            """));
    }

    // ── Yields reached through other statements ────────────────────────────────

    [Fact]
    public async Task A_yield_guarded_by_an_if_streams()
    {
        Assert.Equal("0,2,4", await RunAsync(
            """
            func gen() {
                var i = 0
                while (true) {
                    if (($i % 2) == 0) { yield $i }
                    $i = ($i + 1)
                }
            }
            gen | first 3
            """));
    }

    [Fact]
    public async Task A_yield_inside_a_try_streams()
    {
        Assert.Equal("0,1,2", await RunAsync(
            """
            func gen() {
                var i = 0
                while (true) {
                    try { yield $i } catch { }
                    $i = ($i + 1)
                }
            }
            gen | first 3
            """));
    }

    [Fact]
    public async Task Nested_endless_loops_stream()
    {
        Assert.Equal("0,1,0,1,0", await RunAsync(
            """
            func gen() {
                while (true) {
                    var j = 0
                    while ($j < 2) {
                        yield $j
                        $j = ($j + 1)
                    }
                }
            }
            gen | first 5
            """));
    }

    // ── `return` keeps the values its iteration already produced ───────────────

    [Theory]
    [InlineData("for i in [1, 2, 3, 4] {", "")]
    [InlineData("while ($i < 4) {", "$i = ($i + 1)")]
    [InlineData("until ($i >= 4) {", "$i = ($i + 1)")]
    public async Task Return_delivers_what_the_iteration_already_yielded(string header, string step)
    {
        // `yield $i` then `return` in the same iteration used to lose that `$i`: the signal
        // travelled out past the buffer holding it. Before the streaming fix this lost *every*
        // value, since the whole statement was discarded when the signal escaped ToListAsync.
        Assert.Equal("1,2", await RunAsync(
            $$"""
            func gen() {
                var i = 0
                {{header}}
                    {{step}}
                    yield $i
                    if ($i == 2) { return }
                }
            }
            gen | collect | each { $_ }
            """));
    }

    [Fact]
    public async Task Break_delivers_them_too()
    {
        // The control that was always correct, and the model the `return` fix copied.
        Assert.Equal("1,2", await RunAsync(
            """
            func gen() {
                for i in [1, 2, 3, 4] {
                    yield $i
                    if ($i == 2) { break }
                }
            }
            gen | collect | each { $_ }
            """));
    }

    // ── Nothing that already worked changed ────────────────────────────────────

    [Theory]
    [InlineData("for i in [1, 2, 3] { yield $i }", "1,2,3")]
    [InlineData("var i = 0\n    while ($i < 3) { $i = ($i + 1)\n        yield $i }", "1,2,3")]
    [InlineData("var i = 0\n    until ($i >= 3) { $i = ($i + 1)\n        yield $i }", "1,2,3")]
    public async Task A_finite_generator_still_drains_completely(string body, string expected)
    {
        Assert.Equal(expected, await RunAsync($"func gen() {{ {body} }}\ngen"));
    }

    [Fact]
    public async Task A_loop_that_does_not_yield_is_untouched()
    {
        // Streaming is chosen per statement by whether it can yield, so an ordinary loop must
        // still be collected exactly as before, last-result behaviour included.
        Assert.Equal("2,4,6", await RunAsync("for i in [1, 2, 3] { ($i * 2) }"));
    }

    [Fact]
    public async Task Return_from_a_loop_in_an_ordinary_function_is_unchanged()
    {
        Assert.Equal("99", await RunAsync(
            """
            func f() {
                for i in [1, 2, 3] {
                    if ($i == 2) { return 99 }
                }
                return 0
            }
            f
            """));
    }
}
