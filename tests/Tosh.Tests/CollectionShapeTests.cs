using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Collection shape, as §Collection Shape states it — `TOAST-0018`.
/// </summary>
/// <remarks>
/// <para>
/// Two rules decide whether a collection is one item or many: which values are sequences
/// at all, and whether a sequence arrives alone. The first is settled and specified. The
/// second is a **known defect** — it makes a collection's meaning depend on how many of
/// them there are — and its replacement is `TOAST-0028`.
/// </para>
/// <para>
/// The defect is pinned here rather than left unstated. A test that asserts the wrong
/// behaviour on purpose has to say so, or the next reader will "fix" the test; when
/// `TOAST-0028` lands, `Producing_more_data_changes_what_the_earlier_data_meant` is the
/// one that must flip, and its failure is the signal that it did.
/// </para>
/// </remarks>
public sealed class CollectionShapeTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? string.Empty : results[^1]?.ToString() ?? "null";
    }

    /// <summary>
    /// An array, a set and a range are sequences; a dictionary, a record and a string are
    /// single values.
    /// </summary>
    /// <remarks>
    /// The asymmetry `TS-P3-04` called motivating, and it is the half worth keeping: a
    /// dictionary is a value with named parts rather than a sequence of them.
    /// </remarks>
    [Theory]
    [InlineData("[1, 2, 3]", "3")]
    [InlineData("{: 1, 2, 3 :}", "3")]
    [InlineData("1..3", "3")]
    [InlineData("{% \"a\" => 1, \"b\" => 2 %}", "1")]
    [InlineData("{| a = 1, b = 2 |}", "1")]
    [InlineData("\"abc\"", "1")]
    public async Task Which_values_are_sequences(string literal, string expected)
        => Assert.Equal(expected, await RunAsync($"({literal} | count)"));

    /// <summary>Several collections stay whole; only a lone one expands.</summary>
    [Theory]
    [InlineData("[[1, 2], [3]]", "2")]
    // One level, so a single nested array expands to one item — the inner array.
    [InlineData("[[1, 2]]", "1")]
    public async Task Nested_collections_expand_exactly_one_level(string literal, string expected)
        => Assert.Equal(expected, await RunAsync($"({literal} | count)"));

    /// <summary>
    /// **This asserts a defect on purpose.** `TOAST-0028` flips it.
    /// </summary>
    /// <remarks>
    /// A collection reaching a stage alone is expanded; several are left as items. So
    /// producing a second batch changes what the first batch meant — `count` falls from 3
    /// to 2 as data is *added*, and `first` changes from an element to the whole array.
    ///
    /// When the producer decides shape instead, both rows answer the collection.
    /// </remarks>
    [Fact]
    public async Task Producing_more_data_changes_what_the_earlier_data_meant()
    {
        const string One = "func a() { yield [1, 2, 3] }\n";
        const string Two = "func b() { yield [1, 2, 3]; yield [4] }\n";

        Assert.Equal("3", await RunAsync(One + "(a | count)"));
        Assert.Equal("2", await RunAsync(Two + "(b | count)"));

        // Rendered rather than `ToString`d: an array's `ToString` is `System.Int32[]`,
        // which would pin the host's formatting instead of the value.
        Assert.Equal("1", await RunAsync(One + "echo $\"{(a | first)}\""));
        Assert.Equal("[1, 2, 3]", await RunAsync(Two + "echo $\"{(b | first)}\""));
    }

    /// <summary>
    /// **This asserts a defect on purpose too.** The consumer reads one item further than
    /// it was asked for, so a generator runs a step nobody requested.
    /// </summary>
    /// <remarks>
    /// Deciding "is this the only item?" cannot be done without looking. `TS-P1-08`
    /// removed the half of this lookahead that fires when the first item is not a
    /// collection; the remaining half needs the rule itself to change.
    /// </remarks>
    [Fact]
    public async Task A_generator_is_advanced_further_than_the_consumer_asked()
    {
        const string Source = """
            var produced = 0
            func gen() {
                $produced = $produced + 1
                yield [1, 2]
                $produced = $produced + 1
                yield [3, 4]
            }
            var r = (gen | first 1)
            $produced
            """;

        // One batch was asked for; two were produced.
        Assert.Equal("2", await RunAsync(Source));
    }

    /// <summary>
    /// A collection consumed *as a value* still reaches the command whole.
    /// </summary>
    /// <remarks>
    /// The constraint that defeated the previous attempt at removing the lookahead, kept
    /// here as a control so `TOAST-0028` cannot regress it: `TS-P2-74` records that
    /// spreading every list-valued head made `[] | to json` send nothing downstream
    /// instead of serialising the empty array.
    /// </remarks>
    [Theory]
    [InlineData("[] | to json", "[]")]
    [InlineData("[1] | to json", "[\n  1\n]")]
    public async Task A_collection_consumed_as_a_value_arrives_whole(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// Every command that decides shape reads the "already expanded" marker — `TOAST-0028`
    /// stage 1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// `TS-P2-113` established that a stream whose producer already enumerated a collection
    /// into it must not be expanded again: the lone item it carries is the item, not a
    /// container to spread. That rule was taught to `ReplaySingleInputCollectionAsync` and
    /// **not** to `PeekForTreeAsync`, which carried its own copy of the surrounding logic.
    /// </para>
    /// <para>
    /// So the same stream got two answers depending on which command read it. For
    /// `var r = [[1, 2, 3]]`, `$r | first` gave the inner array while `$r | where true`
    /// gave three integers — and `$r | where true` in a subexpression failed outright with
    /// "requires exactly one object", because it had silently become three.
    /// </para>
    /// <para>
    /// The two copies are now one. These cases are the ones that told them apart.
    /// </para>
    /// </remarks>
    [Theory]
    // The peek path: `where`, `sort`, `filter` and `get` all read through it.
    [InlineData("where true")]
    [InlineData("sort")]
    [InlineData("filter { true }")]
    public async Task A_command_that_peeks_for_a_tree_still_honours_the_expanded_marker(string stage)
    {
        // One item, and that item is the inner array — not three integers. `.Length`
        // rather than `| count`, because a trailing pipeline stage would apply the
        // lone-collection rule again and re-expand what arrives unmarked.
        Assert.Equal(
            "3",
            await RunAsync($"var r = [[1, 2, 3]]\nvar w = ($r | {stage})\necho $\"{{$w.Length}}\""));
    }

    /// <summary>
    /// The marker changes nothing for a stream that never carried it.
    /// </summary>
    /// <remarks>
    /// The negative control for the fix above. Unifying the two copies could have been done
    /// by making the peek path stop expanding at all, which would pass every assertion in
    /// the theory above and break the ordinary case completely.
    /// </remarks>
    [Theory]
    [InlineData("where true", "3")]
    [InlineData("sort", "3")]
    [InlineData("filter { true }", "3")]
    public async Task A_lone_collection_with_no_marker_still_expands(string stage, string expected)
        => Assert.Equal(expected, await RunAsync($"([1, 2, 3] | {stage} | count)"));

    /// <summary>An empty stream stays empty through the peek path.</summary>
    [Theory]
    [InlineData("where true")]
    [InlineData("sort")]
    public async Task An_empty_stream_survives_the_peek_path(string stage)
        => Assert.Equal("0", await RunAsync($"([] | {stage} | count)"));
}
