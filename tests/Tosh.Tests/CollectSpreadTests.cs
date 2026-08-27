using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// <c>collect</c> treats a single collection as its elements — <c>TS-P2-74</c>.
/// </summary>
/// <remarks>
/// <para>
/// The visible symptom was an asymmetry with variables:
/// <c>("a.b.c".Split(".") | collect).Length</c> was <b>1</b>, and the single element was
/// the array itself, while <c>($v | collect).Length</c> on the same array was <b>3</b> —
/// because a variable binding replays as a pipeline and an expression does not. Silent,
/// plausible-looking, and wrong several steps downstream: it made the namespace of every
/// diagnostic code in a generated file come out <c>?</c>.
/// </para>
/// <para>
/// <c>collect</c> was the outlier, not the head. Measured on a bare <c>[1, 2, 3]</c>:
/// <c>count</c> reported 3, <c>each</c> 3, <c>where</c> 2, <c>skip 1</c> 2 and
/// <c>first</c> the first element — every neighbouring stage already read a single
/// collection as its elements, and only <c>collect</c> did not.
/// </para>
/// <para>
/// <b>Fixing it at the pipeline head instead was tried and is wrong.</b> Spreading every
/// list-valued expression head broke eight tests, and <c>[] | to json</c> is the one that
/// settles it: that must serialize the empty array, not send nothing downstream. A head
/// yields one value; whether a collection means itself or its elements is a question for
/// each stage, and this stage belongs with <c>count</c> rather than with <c>to json</c>.
/// </para>
/// </remarks>
public sealed class CollectSpreadTests
{
    private static async Task<IReadOnlyList<object?>> RunAsync(string script)
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);
        return await engine.ExecuteToListAsync(script);
    }

    private static async Task<int> LengthAsync(string script) =>
        Convert.ToInt32(Assert.Single(await RunAsync(script)));

    [Fact]
    public async Task A_method_result_collects_the_same_as_a_variable()
    {
        // The reported case, and the asymmetry itself.
        Assert.Equal(3, await LengthAsync("""("a.b.c".Split(".") | collect).Length"""));
        Assert.Equal(
            3,
            await LengthAsync(
                """
                var v = "a.b.c".Split(".")
                ($v | collect).Length
                """));
    }

    [Fact]
    public async Task A_literal_collection_collects_its_elements()
    {
        Assert.Equal(3, await LengthAsync("([1, 2, 3] | collect).Length"));
    }

    [Fact]
    public async Task Collect_agrees_with_the_stages_beside_it()
    {
        // The whole argument for fixing `collect` rather than the head: these were
        // already unanimous and `collect` was the dissenter.
        Assert.Equal(3, Assert.Single(await RunAsync("[1, 2, 3] | count")));
        Assert.Equal(3, await LengthAsync("([1, 2, 3] | collect).Length"));
        Assert.Equal(3, await LengthAsync("([1, 2, 3] | each { $_ } | collect).Length"));
        Assert.Equal(2, await LengthAsync("([1, 2, 3] | where { $_ > 1 } | collect).Length"));
        Assert.Equal(2, await LengthAsync("([1, 2, 3] | skip 1 | collect).Length"));
    }

    [Fact]
    public async Task A_genuinely_multi_item_pipeline_is_unchanged()
    {
        // Written without nesting the command in its own parentheses: `(seq 1 3)` inside
        // an argument is a multi-value subexpression and correctly hits TS-P1-20's rule,
        // which is a different thing from what this test is about.
        var results = await RunAsync(
            """
            var c = (seq 1 3 | collect)
            $c.Length
            """);

        Assert.Equal(3, Convert.ToInt32(Assert.Single(results)));
    }

    [Fact]
    public async Task A_pipeline_of_collections_keeps_them_as_items()
    {
        // Only a *single* incoming collection is read as its elements. Several stay
        // several, so `collect` is not a flatten.
        var results = await RunAsync(
            """
            var rows = [[1, 2], [3, 4]]
            ($rows | collect).Length
            """);

        Assert.Equal(2, Convert.ToInt32(Assert.Single(results)));
    }

    // ── The case that ruled out fixing this at the head ────────────────────────

    [Theory]
    [InlineData("[] | to json", "[]")]
    [InlineData("[1, 2] | to json --compact", "[1,2]")]
    public async Task A_value_stage_still_sees_the_whole_collection(string script, string expected)
    {
        // `to json` serializes the value it is given. Spreading list-valued heads would
        // have made `[] | to json` emit nothing at all.
        var results = await RunAsync(script);

        Assert.Equal(expected, Assert.Single(results)?.ToString()?.Trim());
    }

    [Fact]
    public async Task A_string_is_not_a_collection_of_characters()
    {
        // `IEnumerable` would include strings; they are excluded, or `"abc" | collect`
        // would become three characters.
        Assert.Equal(1, await LengthAsync("""("abc" | collect).Length"""));
    }

    [Fact]
    public async Task A_record_is_one_value_not_its_fields()
    {
        Assert.Equal(1, await LengthAsync("""({| a = 1, b = 2 |} | collect).Length"""));
    }
}
