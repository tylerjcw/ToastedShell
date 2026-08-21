using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A short-circuiting consumer pulls exactly what it needs — <c>TS-P1-08</c>.
/// </summary>
/// <remarks>
/// <para>
/// `gen | first 1` resumed the generator twice. Not in <c>FirstCommand</c>, which breaks
/// immediately after emitting its last item, but in
/// <c>ShellIterationUtilities.ReplaySingleInputCollectionAsync</c>: it pulled a second
/// item to decide whether the input was a *lone* collection that should be expanded
/// element-wise.
/// </para>
/// <para>
/// That lookahead only earns its cost when the first item is expandable. Expanding a
/// scalar yields the scalar, so for a generator of numbers the second pull answered a
/// question with no consequence — while costing an extra unit of the producer's work, and
/// surfacing an error if the surplus item happened to throw.
/// </para>
/// <para>
/// Pull counts are measured through <c>writeline</c>, which writes directly rather than
/// into the pipeline. An earlier attempt used <c>echo</c> and measured nothing: those
/// values *are* the pipeline, so `first 1` consumed them and the count was invisible.
/// </para>
/// </remarks>
public sealed class ShortCircuitPullTests
{
    private const string Generator =
        """
        func gen() {
            writeline "p1"
            yield 1
            writeline "p2"
            yield 2
            writeline "p3"
            yield 3
        }
        """;

    /// <summary>
    /// Runs <paramref name="consumer"/> against the probe generator and returns how many
    /// items it caused to be produced.
    /// </summary>
    private static async Task<int> ProducedCountAsync(string consumer)
    {
        var writer = new StringWriter();
        var runtime = ToshRuntime.CreateDefault();
        runtime.Output = writer;

        var engine = new ToshEngine(runtime);
        await engine.ExecuteToListAsync($"{Generator}\n{consumer}");

        return writer.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.TrimEnd('\r').StartsWith('p'));
    }

    [Theory]
    // `first 1` needs one item. It pulled two.
    [InlineData("gen | first 1 | ignore", 1)]
    [InlineData("gen | first 2 | ignore", 2)]
    // `any` decides on the first matching item.
    [InlineData("gen | any { _ > 0 } | ignore", 1)]
    // `take-while` must evaluate the item that fails the predicate to know to stop, so
    // three is correct here rather than surplus — included so the distinction is recorded
    // and a future "optimisation" does not break it.
    [InlineData("gen | take-while { _ < 3 } | ignore", 3)]
    public async Task A_consumer_pulls_only_what_it_needs(string consumer, int expected)
    {
        Assert.Equal(expected, await ProducedCountAsync(consumer));
    }

    // ── The semantics the lookahead existed to provide ─────────────────────────

    private static async Task<object?> EvaluateAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? null : results[^1];
    }

    [Theory]
    // A sequence expands element-wise. `TOAST-0028` changed *which* streams are sequences,
    // not what happens to one: an expression head is a sequence, and `...` makes any value
    // into one. What no longer qualifies is a collection that merely arrived alone.
    [InlineData("echo ...[1, 2, 3] | first 2 | join \",\"", "1,2")]
    [InlineData("[1, 2, 3] | first 2 | join \",\"", "1,2")]
    [InlineData("echo a b c | first 2 | join \",\"", "a,b")]
    public async Task A_sequence_expands_element_wise(string source, string expected)
    {
        Assert.Equal(expected, await EvaluateAsync(source));
    }

    [Theory]
    // `TOAST-0028`. The producer decides, and `echo` yields its argument as a value — so
    // one collection argument is one item. This case read 3 until 2026-08-21, and it read 3
    // by *counting*: the collection was alone, so the consumer spread it.
    [InlineData("echo [1, 2, 3] | count", 1)]
    // Which is why two collections were already two items rather than six. That answer is
    // unchanged; what changed is that it no longer disagrees with the line above.
    [InlineData("echo [1,2] [3,4] | count", 2)]
    // Records and strings are atoms, not sequences of fields or characters.
    [InlineData("echo {| a = 1 |} | count", 1)]
    [InlineData("echo \"abc\" | count", 1)]
    public async Task A_collection_yielded_by_a_command_is_one_item(string source, int expected)
    {
        Assert.Equal(expected, await EvaluateAsync(source));
    }
    [Fact]
    public async Task An_infinite_generator_still_short_circuits()
    {
        // TS-P1-08's headline symptom, which turned out to be already fixed by the
        // parser repair earlier in this programme — `| take-while …` had been swallowed
        // into the lambda body, leaving the generator unbounded and reaching 104 GB.
        // Pinned here as well as in LazySequenceTests, because that test asserts values
        // while this file asserts the pull discipline that keeps it bounded.
        var results = await EvaluateAsync(
            "recur (0, 1) func(a, b) => ($a + $b) | take-while { _ < 100 } | join \",\"");

        Assert.Equal("0,1,1,2,3,5,8,13,21,34,55,89", results);
    }
}
