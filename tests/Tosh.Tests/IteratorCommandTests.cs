using Tosh.Language;

namespace Tosh.Tests;

public class IteratorCommandTests
{
    // ================================================================
    // Tier 1: Infinite generators — cycle, repeat, repeatedly
    // ================================================================

    [Fact]
    public async Task Cycle_repeats_sequence()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo 1 2 3 | cycle | first 9");
        Assert.Equal(new object[] { 1, 2, 3, 1, 2, 3, 1, 2, 3 }, results);
    }

    [Fact]
    public async Task Cycle_single_item()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo 42 | cycle | first 4");
        Assert.Equal(new object[] { 42, 42, 42, 42 }, results);
    }

    [Fact]
    public async Task Cycle_empty_input_yields_nothing()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo | cycle | first 3");
        Assert.Empty(results);
    }

    [Fact]
    public async Task Repeat_infinite()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("repeat 7 | first 5");
        Assert.Equal(new object[] { 7, 7, 7, 7, 7 }, results);
    }

    [Fact]
    public async Task Repeat_with_count()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("repeat hello 3");
        Assert.Equal(new object[] { "hello", "hello", "hello" }, results);
    }

    [Fact]
    public async Task Repeat_zero_count()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("repeat x 0");
        Assert.Empty(results);
    }

    [Fact]
    public async Task Repeat_string_value()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("repeat \"na\" | first 4");
        Assert.Equal(new object[] { "na", "na", "na", "na" }, results);
    }

    [Fact]
    public async Task Repeatedly_evaluates_each_time()
    {
        var engine = new ToshEngine();
        // Each invocation gets the 0-based index as a long
        var results = await engine.ExecuteToListAsync("repeatedly func(i) => ($i * $i) | first 5");
        Assert.Equal(5, results.Count);
        Assert.Equal(0L, Convert.ToInt64(results[0]));
        Assert.Equal(1L, Convert.ToInt64(results[1]));
        Assert.Equal(4L, Convert.ToInt64(results[2]));
        Assert.Equal(9L, Convert.ToInt64(results[3]));
        Assert.Equal(16L, Convert.ToInt64(results[4]));
    }

    [Fact]
    public async Task Repeatedly_with_block()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("repeatedly { $_ + 10 } | first 4");
        Assert.Equal(4, results.Count);
        Assert.Equal(10L, Convert.ToInt64(results[0]));
        Assert.Equal(11L, Convert.ToInt64(results[1]));
        Assert.Equal(12L, Convert.ToInt64(results[2]));
        Assert.Equal(13L, Convert.ToInt64(results[3]));
    }

    // ================================================================
    // Tier 2: Pipeline adaptors — enumerate, dedup, intersperse, step-by
    // ================================================================

    [Fact]
    public async Task Enumerate_zero_based()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo a b c | enumerate | each { $_ | to json -c }");
        Assert.Equal(3, results.Count);
        Assert.Equal("[0,\"a\"]", results[0]?.ToString());
        Assert.Equal("[1,\"b\"]", results[1]?.ToString());
        Assert.Equal("[2,\"c\"]", results[2]?.ToString());
    }

    [Fact]
    public async Task Enumerate_custom_start()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo x y | enumerate 5 | each { $_ | to json -c }");
        Assert.Equal("[5,\"x\"]", results[0]?.ToString());
        Assert.Equal("[6,\"y\"]", results[1]?.ToString());
    }

    [Fact]
    public async Task Enumerate_with_infinite_source()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("1.. | enumerate | first 3 | each { $_ | to json -c }");
        Assert.Equal("[0,1]", results[0]?.ToString());
        Assert.Equal("[1,2]", results[1]?.ToString());
        Assert.Equal("[2,3]", results[2]?.ToString());
    }

    [Fact]
    public async Task Dedup_removes_consecutive_duplicates()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo 1 1 2 2 3 1 1 | dedup");
        Assert.Equal(new object[] { 1, 2, 3, 1 }, results);
    }

    [Fact]
    public async Task Dedup_preserves_non_adjacent_duplicates()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo a b a b | dedup");
        Assert.Equal(new object[] { "a", "b", "a", "b" }, results);
    }

    [Fact]
    public async Task Dedup_single_element()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo 1 | dedup");
        Assert.Single(results);
        Assert.Equal(1, results[0]);
    }

    [Fact]
    public async Task Intersperse_inserts_separator()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo 1 2 3 | intersperse 0");
        Assert.Equal(new object[] { 1, 0, 2, 0, 3 }, results);
    }

    [Fact]
    public async Task Intersperse_string_separator()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo a b c | intersperse \"-\"");
        Assert.Equal(new object[] { "a", "-", "b", "-", "c" }, results);
    }

    [Fact]
    public async Task Intersperse_single_item_no_separator()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo x | intersperse 0");
        Assert.Single(results);
        Assert.Equal("x", results[0]);
    }

    [Fact]
    public async Task Intersperse_with_infinite_source()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("1.. | intersperse 0 | first 7");
        Assert.Equal(new object[] { 1, 0, 2, 0, 3, 0, 4 }, results);
    }

    [Fact]
    public async Task Step_by_every_third()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("1.. | step-by 3 | first 5");
        Assert.Equal(new object[] { 1, 4, 7, 10, 13 }, results);
    }

    [Fact]
    public async Task Step_by_every_other()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo a b c d e f | step-by 2");
        Assert.Equal(new object[] { "a", "c", "e" }, results);
    }

    [Fact]
    public async Task Step_by_one_is_identity()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo 1 2 3 | step-by 1");
        Assert.Equal(new object[] { 1, 2, 3 }, results);
    }

    // ================================================================
    // Tier 3: Combinatorial — chain, cartesian-product, combinations, permutations
    // ================================================================

    [Fact]
    public async Task Chain_concatenates_sequences()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo 1 2 | chain [3, 4] [5, 6]");
        Assert.Equal(new object[] { 1, 2, 3, 4, 5, 6 }, results);
    }

    [Fact]
    public async Task Chain_with_array()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo a b | chain [c, d, e]");
        Assert.Equal(new object[] { "a", "b", "c", "d", "e" }, results);
    }

    [Fact]
    public async Task Cartesian_product_finite()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("[1, 2] | cartesian-product [a, b] | each { $_ | to json -c }");
        var items = results.Select(x => x?.ToString()).ToList();
        Assert.Contains("[1,\"a\"]", items);
        Assert.Contains("[1,\"b\"]", items);
        Assert.Contains("[2,\"a\"]", items);
        Assert.Contains("[2,\"b\"]", items);
        Assert.Equal(4, items.Count);
    }

    [Fact]
    public async Task Cartesian_product_with_combiner()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo 1 2 3 | cartesian-product [10, 20] func(a, b) => ($a * $b)");
        var items = results.Cast<object>().Select(Convert.ToInt32).OrderBy(x => x).ToList();
        Assert.Equal(new[] { 10, 20, 20, 30, 40, 60 }.OrderBy(x => x).ToList(), items);
    }

    [Fact]
    public async Task Combinations_k2_of_3()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("[1, 2, 3] | combinations 2 | each { $_ | to json -c }");
        var items = results.Select(x => x?.ToString()).ToList();
        Assert.Equal(3, items.Count);
        Assert.Equal("[1,2]", items[0]);
        Assert.Equal("[1,3]", items[1]);
        Assert.Equal("[2,3]", items[2]);
    }

    [Fact]
    public async Task Combinations_k0_yields_one_result()
    {
        var engine = new ToshEngine();
        // k=0 yields one empty-array result, which expands to nothing in pipeline
        var results = await engine.ExecuteToListAsync("[1, 2, 3] | combinations 0 | collect");
        Assert.Single(results);
    }

    [Fact]
    public async Task Combinations_k_larger_than_n_yields_nothing()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("[1, 2] | combinations 5");
        Assert.Empty(results);
    }

    [Fact]
    public async Task Permutations_full_length()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("[1, 2, 3] | permutations | each { $_ | to json -c }");
        Assert.Equal(6, results.Count); // 3! = 6
        var items = results.Select(x => x?.ToString()).ToHashSet();
        Assert.Contains("[1,2,3]", items);
        Assert.Contains("[1,3,2]", items);
        Assert.Contains("[2,1,3]", items);
        Assert.Contains("[2,3,1]", items);
        Assert.Contains("[3,1,2]", items);
        Assert.Contains("[3,2,1]", items);
    }

    [Fact]
    public async Task Permutations_k2_of_3()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("[a, b, c] | permutations 2 | each { $_ | to json -c }");
        Assert.Equal(6, results.Count); // P(3,2) = 6
        var items = results.Select(x => x?.ToString()).ToHashSet();
        Assert.Contains("[\"a\",\"b\"]", items);
        Assert.Contains("[\"b\",\"a\"]", items);
        Assert.Contains("[\"a\",\"c\"]", items);
        Assert.Contains("[\"c\",\"a\"]", items);
        Assert.Contains("[\"b\",\"c\"]", items);
        Assert.Contains("[\"c\",\"b\"]", items);
    }

    [Fact]
    public async Task Permutations_k0_yields_one_result()
    {
        var engine = new ToshEngine();
        // k=0 yields one empty-array result, which expands to nothing in pipeline
        var results = await engine.ExecuteToListAsync("[1, 2, 3] | permutations 0 | collect");
        Assert.Single(results);
    }

    // ================================================================
    // Infinite nested comprehension fixes
    // ================================================================

    [Fact]
    public async Task List_comprehension_with_infinite_source_errors()
    {
        var engine = new ToshEngine();
        var ex = await Assert.ThrowsAsync<Tosh.Core.ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("[$x <| for x in 1..]"));
        Assert.Contains("infinite", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Generator_comprehension_nested_infinite_diagonal()
    {
        var engine = new ToshEngine();
        // Diagonal enumeration: should produce pairs from multiple x AND y values
        var results = await engine.ExecuteToListAsync("($x * $y <| for x in 1.. for y in 1..) | first 10");
        Assert.Equal(10, results.Count);

        // With diagonal enumeration, we should see products from different x,y pairs
        // Diagonal d: pairs where i+j==d (0-based cache indices, 1-based values)
        // d=0: (1,1)=1  d=1: (1,2)=2,(2,1)=2  d=2: (1,3)=3,(2,2)=4,(3,1)=3  ...
        var products = results.Cast<object>().Select(Convert.ToInt32).ToList();
        // First result is always 1*1=1
        Assert.Equal(1, products[0]);
        // Must contain a product > 1 that isn't just sequential (proving x>1 was reached)
        Assert.True(products.Any(p => p == 4), "Should contain 2*2=4 from diagonal enumeration");
    }

    [Fact]
    public async Task Generator_comprehension_finite_nested_still_works()
    {
        var engine = new ToshEngine();
        // Finite nested comprehension should still use normal nested loops
        var results = await engine.ExecuteToListAsync("($x * $y <| for x in [1, 2, 3] for y in [10, 20]) | each { $_ }");
        var items = results.Cast<object>().Select(Convert.ToInt32).OrderBy(x => x).ToList();
        Assert.Equal(new[] { 10, 20, 20, 30, 40, 60 }.OrderBy(x => x).ToList(), items);
    }

    // ================================================================
    // Composition tests: combining new commands with infinite sources
    // ================================================================

    [Fact]
    public async Task Cycle_with_step_by()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo 1 2 3 | cycle | step-by 2 | first 6");
        // cycle: 1,2,3,1,2,3,1,2,3,1,2,3,...
        // step-by 2: 1,3,2,1,3,2
        Assert.Equal(new object[] { 1, 3, 2, 1, 3, 2 }, results);
    }

    [Fact]
    public async Task Repeat_with_enumerate()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("repeat x | enumerate | first 3 | each { $_ | to json -c }");
        Assert.Equal("[0,\"x\"]", results[0]?.ToString());
        Assert.Equal("[1,\"x\"]", results[1]?.ToString());
        Assert.Equal("[2,\"x\"]", results[2]?.ToString());
    }

    [Fact]
    public async Task Infinite_range_with_dedup()
    {
        var engine = new ToshEngine();
        // Each item in 1.. is unique, so dedup is identity
        var results = await engine.ExecuteToListAsync("1.. | dedup | first 5");
        Assert.Equal(new object[] { 1, 2, 3, 4, 5 }, results);
    }

    [Fact]
    public async Task Chain_with_finite_range()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo a b c | chain (1..5)");
        Assert.Equal(new object[] { "a", "b", "c", 1, 2, 3, 4, 5 }, results);
    }
}
