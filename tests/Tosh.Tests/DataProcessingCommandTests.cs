using Tosh.Runtime;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class DataProcessingCommandTests
{
    // ── chunk ──

    [Fact]
    public async Task Chunk_groups_into_fixed_size_batches()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var results = await engine.ExecuteToListAsync("[1, 2, 3, 4, 5] | chunk 2");

        Assert.Equal(3, results.Count);
        Assert.Equal([1, 2], ((object?[])results[0]!).Select(Convert.ToInt64).ToArray());
        Assert.Equal([3, 4], ((object?[])results[1]!).Select(Convert.ToInt64).ToArray());
        Assert.Equal([5], ((object?[])results[2]!).Select(Convert.ToInt64).ToArray());
    }

    [Fact]
    public async Task Chunk_exact_multiple_produces_full_batches()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var results = await engine.ExecuteToListAsync("[1, 2, 3, 4] | chunk 2");

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task Chunk_empty_input_produces_nothing()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var results = await engine.ExecuteToListAsync("[] | chunk 3");

        Assert.Empty(results);
    }

    // ── window ──

    [Fact]
    public async Task Window_yields_sliding_windows()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var results = await engine.ExecuteToListAsync("[1, 2, 3, 4, 5] | window 3");

        Assert.Equal(3, results.Count);
        Assert.Equal([1, 2, 3], ((object?[])results[0]!).Select(Convert.ToInt64).ToArray());
        Assert.Equal([2, 3, 4], ((object?[])results[1]!).Select(Convert.ToInt64).ToArray());
        Assert.Equal([3, 4, 5], ((object?[])results[2]!).Select(Convert.ToInt64).ToArray());
    }

    [Fact]
    public async Task Window_with_combiner_block()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        // Combiner receives the window array as _; use .Length member access
        var results = await engine.ExecuteToListAsync("[1, 2, 3, 4, 5] | window 3 { _.Length }");

        Assert.Equal([3, 3, 3], results.Select(Convert.ToInt64).ToArray());
    }

    [Fact]
    public async Task Window_smaller_input_than_size_produces_nothing()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var results = await engine.ExecuteToListAsync("[1, 2] | window 5");

        Assert.Empty(results);
    }

    // ── group-while ──

    [Fact]
    public async Task GroupWhile_splits_on_predicate_failure()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        // Items 1,2,3 all match _ < 10; then 10 fails; starts new group; 11 also fails; new group; etc.
        var results = await engine.ExecuteToListAsync("[1, 2, 3, 10, 11] | group-while { _ < 10 }");

        // Consecutive matching: [1,2,3], then [10] (fails, starts new group), then [11] (fails, starts new group)
        // Actually: 1 < 10 true, 2 < 10 true, 3 < 10 true, 10 < 10 false → flush [1,2,3]; start [10]; 11 < 10 false → flush [10]; start [11]; end → flush [11]
        Assert.Equal(3, results.Count);
        Assert.Equal([1, 2, 3], ((object?[])results[0]!).Select(Convert.ToInt64).ToArray());
    }

    [Fact]
    public async Task GroupWhile_single_group_when_predicate_always_true()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var results = await engine.ExecuteToListAsync("[1, 2, 3] | group-while { _ > 0 }");

        Assert.Single(results);
        Assert.Equal([1, 2, 3], ((object?[])results[0]!).Select(Convert.ToInt64).ToArray());
    }

    // ── frequencies ──

    [Fact]
    public async Task Frequencies_counts_occurrences()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var results = await engine.ExecuteToListAsync("[\"a\", \"b\", \"a\", \"c\", \"b\", \"a\"] | frequencies");

        Assert.Equal(3, results.Count);

        var first = (IDictionary<string, object?>)results[0]!;
        Assert.Equal("a", first["Value"]);
        Assert.Equal(3, Convert.ToInt32(first["Count"]));

        var second = (IDictionary<string, object?>)results[1]!;
        Assert.Equal("b", second["Value"]);
        Assert.Equal(2, Convert.ToInt32(second["Count"]));
    }

    [Fact]
    public async Task Frequencies_preserves_insertion_order()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var results = await engine.ExecuteToListAsync("[\"x\", \"y\", \"x\"] | frequencies");

        var first = (IDictionary<string, object?>)results[0]!;
        var second = (IDictionary<string, object?>)results[1]!;
        Assert.Equal("x", first["Value"]);
        Assert.Equal("y", second["Value"]);
    }

    // ── interleave ──

    [Fact]
    public async Task Interleave_alternates_two_sequences()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        await engine.ExecuteToListAsync("var other = [\"a\", \"b\", \"c\"]");
        var results = await engine.ExecuteToListAsync("[1, 2, 3] | interleave $other");

        var stringified = results.Select(x => x?.ToString() ?? string.Empty).ToArray();
        Assert.Equal(["1", "a", "2", "b", "3", "c"], stringified);
    }

    [Fact]
    public async Task Interleave_drains_longer_other_sequence()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        await engine.ExecuteToListAsync("var other = [\"a\", \"b\", \"c\", \"d\"]");
        var results = await engine.ExecuteToListAsync("[1] | interleave $other");

        // 1, a, b, c, d
        Assert.Equal(5, results.Count);
    }
}
