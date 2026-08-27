using Tosh.Runtime;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class FunctionalPipelineCommandTests(ToshRuntimeFixture fixture) : IClassFixture<ToshRuntimeFixture>
{
    // ── flat-map ──

    [Fact]
    public async Task FlatMap_flattens_nested_arrays_from_block()
    {
        var engine = new ToshEngine(fixture.Runtime.Language);

        var results = await engine.ExecuteToListAsync("[1, 2, 3] | flat-map { [_, (_ * 10)] }");

        Assert.Equal([1, 10, 2, 20, 3, 30], results.Select(Convert.ToInt64).ToArray());
    }

    [Fact]
    public async Task FlatMap_passes_through_non_collection_results()
    {
        var engine = new ToshEngine(fixture.Runtime.Language);

        var results = await engine.ExecuteToListAsync("[1, 2, 3] | flat-map { _ * 2 }");

        Assert.Equal([2, 4, 6], results.Select(Convert.ToInt64).ToArray());
    }

    [Fact]
    public async Task FlatMap_with_anonymous_function()
    {
        var engine = new ToshEngine(fixture.Runtime.Language);

        var results = await engine.ExecuteToListAsync("[1, 2, 3] | flat-map { [_, _] }");

        Assert.Equal([1, 1, 2, 2, 3, 3], results.Select(Convert.ToInt64).ToArray());
    }

    // ── zip ──

    [Fact]
    public async Task Zip_pairs_two_sequences()
    {
        var engine = new ToshEngine(fixture.Runtime.Language);

        await engine.ExecuteToListAsync("var b = [\"a\", \"b\", \"c\"]");
        var results = await engine.ExecuteToListAsync("[1, 2, 3] | zip $b");

        Assert.Equal(3, results.Count);
        var first = Assert.IsType<object?[]>(results[0]);
        Assert.Equal(1L, Convert.ToInt64(first[0]));
        Assert.Equal("a", first[1]);
    }

    [Fact]
    public async Task Zip_stops_at_shorter_sequence()
    {
        var engine = new ToshEngine(fixture.Runtime.Language);

        await engine.ExecuteToListAsync("var b = [\"x\"]");
        var results = await engine.ExecuteToListAsync("[1, 2, 3] | zip $b");

        Assert.Single(results);
    }

    [Fact]
    public async Task Zip_with_combiner_block()
    {
        var engine = new ToshEngine(fixture.Runtime.Language);

        await engine.ExecuteToListAsync("var b = [10, 20, 30]");
        var results = await engine.ExecuteToListAsync("[1, 2, 3] | zip $b { _ + $acc }");

        Assert.Equal([11, 22, 33], results.Select(Convert.ToInt64).ToArray());
    }

    [Fact]
    public async Task Zip_exposes_clearer_other_side_locals_in_combiner_block()
    {
        var engine = new ToshEngine(fixture.Runtime.Language);

        await engine.ExecuteToListAsync("var b = [10, 20, 30]");
        var results = await engine.ExecuteToListAsync("[1, 2, 3] | zip $b { $left + $other + $right }");

        Assert.Equal([21, 42, 63], results.Select(Convert.ToInt64).ToArray());
    }

    // ── scan ──

    [Fact]
    public async Task Scan_yields_running_totals()
    {
        var engine = new ToshEngine(fixture.Runtime.Language);

        var results = await engine.ExecuteToListAsync("[1, 2, 3, 4] | scan 0 { $acc + _ }");

        Assert.Equal([1, 3, 6, 10], results.Select(Convert.ToInt64).ToArray());
    }

    [Fact]
    public async Task Scan_with_string_accumulator()
    {
        var engine = new ToshEngine(fixture.Runtime.Language);

        var results = await engine.ExecuteToListAsync("[\"a\", \"b\", \"c\"] | scan \"\" { $acc + _ }");

        Assert.Equal(new object[] { "a", "ab", "abc" }, results.Cast<object>().ToArray());
    }

    [Fact]
    public async Task Scan_yields_nothing_for_empty_input()
    {
        var engine = new ToshEngine(fixture.Runtime.Language);

        var results = await engine.ExecuteToListAsync("[] | scan 0 { $acc + _ }");

        Assert.Empty(results);
    }
}
