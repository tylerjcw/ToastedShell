using System.Dynamic;
using Tosh.Core;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class LanguageFeatureTests
{
    // ── Destructuring: Array ──

    [Fact]
    public async Task ArrayDestructuring_binds_elements()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync("var [a, b, c] = [10, 20, 30]");
        var results = await engine.ExecuteToListAsync("echo $a $b $c");

        Assert.Equal([10L, 20L, 30L], results.Select(Convert.ToInt64).ToArray());
    }

    [Fact]
    public async Task ArrayDestructuring_missing_elements_are_null()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync("var [x, y, z] = [1]");
        var results = await engine.ExecuteToListAsync("echo $x");

        Assert.Equal(1L, Convert.ToInt64(results[0]));

        var nullResults = await engine.ExecuteToListAsync("echo $y");
        Assert.Null(nullResults[0]);
    }

    [Fact]
    public async Task ArrayDestructuring_from_pipeline()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync("var [first, second] = echo hello world");
        var results = await engine.ExecuteToListAsync("echo $first $second");

        Assert.Equal("hello", results[0]?.ToString());
        Assert.Equal("world", results[1]?.ToString());
    }

    // ── Destructuring: Record ──

    [Fact]
    public async Task RecordDestructuring_binds_fields()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync("var rec = { Name = Alice, Age = 30 }");
        await engine.ExecuteToListAsync("var { Name, Age } = $rec");
        var results = await engine.ExecuteToListAsync("echo $Name $Age");

        Assert.Equal("Alice", results[0]?.ToString());
        Assert.Equal(30L, Convert.ToInt64(results[1]));
    }

    [Fact]
    public async Task RecordDestructuring_missing_fields_are_null()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync("var rec = { X = 1 }");
        await engine.ExecuteToListAsync("var { X, Y } = $rec");

        var xResult = await engine.ExecuteToListAsync("echo $X");
        Assert.Equal(1L, Convert.ToInt64(xResult[0]));

        var yResult = await engine.ExecuteToListAsync("echo $Y");
        Assert.Null(yResult[0]);
    }

    // ── Spread: Array ──

    [Fact]
    public async Task Spread_in_array_literal()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync("var a = [1, 2, 3]");
        var results = await engine.ExecuteToListAsync("[0, ...$a, 4] | each { _ }");

        Assert.Equal([0, 1, 2, 3, 4], results.Select(Convert.ToInt64).ToArray());
    }

    [Fact]
    public async Task Spread_concatenates_two_arrays()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync("var x = [1, 2]");
        await engine.ExecuteToListAsync("var y = [3, 4]");
        var results = await engine.ExecuteToListAsync("[...$x, ...$y] | each { _ }");

        Assert.Equal([1, 2, 3, 4], results.Select(Convert.ToInt64).ToArray());
    }

    // ── Spread: Record ──

    [Fact]
    public async Task Spread_in_record_literal()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync("var base = { Name = Tosh, Version = 1 }");
        var results = await engine.ExecuteToListAsync("{ ...$base, Version = 2 }");

        var record = Assert.IsAssignableFrom<IDictionary<string, object?>>(results[0]);
        Assert.Equal("Tosh", record["Name"]?.ToString());
        Assert.Equal(2L, Convert.ToInt64(record["Version"]));
    }

    [Fact]
    public async Task Spread_merges_multiple_records()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync("var a = { X = 1 }");
        await engine.ExecuteToListAsync("var b = { Y = 2 }");
        var results = await engine.ExecuteToListAsync("{ ...$a, ...$b }");

        var record = Assert.IsAssignableFrom<IDictionary<string, object?>>(results[0]);
        Assert.Equal(1L, Convert.ToInt64(record["X"]));
        Assert.Equal(2L, Convert.ToInt64(record["Y"]));
    }

    // ── Computed Property Names ──

    [Fact]
    public async Task Computed_property_name_from_variable()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync("var key = echo status");
        var results = await engine.ExecuteToListAsync("{ ($key) = active }");

        var record = Assert.IsAssignableFrom<IDictionary<string, object?>>(results[0]);
        Assert.Equal("active", record["status"]?.ToString());
    }

    [Fact]
    public async Task Computed_property_with_static_fields()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync("var k = echo color");
        var results = await engine.ExecuteToListAsync("{ Name = widget, ($k) = blue }");

        var record = Assert.IsAssignableFrom<IDictionary<string, object?>>(results[0]);
        Assert.Equal("widget", record["Name"]?.ToString());
        Assert.Equal("blue", record["color"]?.ToString());
    }

    // ── Function References ──

    [Fact]
    public async Task Function_reference_returns_callable()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        // Define a function, then get a reference to it
        await engine.ExecuteToListAsync("func greet(name) { echo hello $name }");
        var results = await engine.ExecuteToListAsync("var f = &greet\n$f.Name");

        Assert.Equal("greet", results[0]?.ToString());
    }

    [Fact]
    public async Task Function_reference_to_builtin()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("var f = &sum\n$f.Name");

        Assert.Equal("sum", results[0]?.ToString());
    }

    [Fact]
    public async Task Function_reference_with_pipe()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        // Use &map to get a reference, confirm it's a command
        await engine.ExecuteToListAsync("var m = &map");
        var results = await engine.ExecuteToListAsync("$m.Name");

        Assert.Equal("map", results[0]?.ToString());
    }

    // ── Index Access ──

    [Fact]
    public async Task Index_access_returns_array_item()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            var x = [1, 2, 3, 4, 5]
            $x[3]
            """);

        Assert.Equal(4L, Convert.ToInt64(results[0]));
    }

    [Fact]
    public async Task Index_access_can_chain_into_member_access()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            var rows = [{ Name = alpha }, { Name = beta }]
            $rows[1].Name
            """);

        Assert.Equal("beta", results[0]?.ToString());
    }

    [Fact]
    public async Task Index_access_returns_string_character()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            var text = "toast"
            $text[1]
            """);

        Assert.Equal('o', Assert.IsType<char>(results[0]));
    }

    [Fact]
    public async Task Index_access_can_lookup_record_values_by_key()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            var x = { Name = Tosh, Version = 1 }
            $x["Name"]
            $x["Version",]
            """);

        Assert.Equal("Tosh", results[0]?.ToString());
        Assert.Equal(1L, Convert.ToInt64(results[1]));
    }

    [Fact]
    public async Task Index_access_can_lookup_record_keys_by_value()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            var x = { Name = Tosh, Version = 1 }
            $x[,1]
            """);

        Assert.Equal("Version", results[0]?.ToString());
    }

    // ── Compose ──

    [Fact]
    public async Task Compose_chains_two_functions()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync("func dbl(x) { return ($x * 2) }");
        await engine.ExecuteToListAsync("func inc(x) { return ($x + 1) }");
        await engine.ExecuteToListAsync("var f = compose &dbl &inc");
        var results = await engine.ExecuteToListAsync("invoke $f 3");

        // dbl(3) = 6, then inc(6) = 7
        Assert.Equal(7L, Convert.ToInt64(results[0]));
    }

    [Fact]
    public async Task Compose_chains_three_functions()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync("func a(x) { return ($x + 1) }");
        await engine.ExecuteToListAsync("func b(x) { return ($x * 2) }");
        await engine.ExecuteToListAsync("func c(x) { return ($x - 5) }");
        await engine.ExecuteToListAsync("var f = compose &a &b &c");
        var results = await engine.ExecuteToListAsync("invoke $f 10");

        // a(10) = 11, b(11) = 22, c(22) = 17
        Assert.Equal(17L, Convert.ToInt64(results[0]));
    }

    [Fact]
    public async Task Compose_preserves_callable_metadata()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync("func dbl(x) { return ($x * 2) }");
        await engine.ExecuteToListAsync("func inc(x) { return ($x + 1) }");
        await engine.ExecuteToListAsync("var f = compose &dbl &inc");
        var results = await engine.ExecuteToListAsync("$f.Name");

        Assert.Contains("compose", results[0]?.ToString());
    }

    // ── Assert ──

    [Fact]
    public async Task Assert_passes_when_true()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync("var x = echo 10");
        // Should not throw
        await engine.ExecuteToListAsync("assert { $x == 10 }");
    }

    [Fact]
    public async Task Assert_throws_when_false()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync("var x = echo 5");

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => engine.ExecuteToListAsync("assert { $x > 100 }"));

        Assert.Contains("assertion failed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Assert_throws_with_custom_message()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync("var x = echo 0");

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => engine.ExecuteToListAsync("assert { $x > 0 } \"x must be positive\""));

        Assert.Contains("x must be positive", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Defer ──

    [Fact]
    public async Task Defer_runs_after_block_exits()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        // Defer should not affect the function's return value
        await engine.ExecuteToListAsync(
            "func test() {\n" +
            "  defer { echo cleanup }\n" +
            "  echo result\n" +
            "}");
        var results = await engine.ExecuteToListAsync("test");

        // Only "result" comes through — defer output is discarded
        Assert.Single(results);
        Assert.Equal("result", results[0]?.ToString());
    }

    [Fact]
    public async Task Defer_runs_even_on_exception()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        // The original exception should still propagate through defers
        await engine.ExecuteToListAsync(
            "func risky() {\n" +
            "  defer { echo would-cleanup }\n" +
            "  throw \"boom\"\n" +
            "}");

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => engine.ExecuteToListAsync("risky"));

        Assert.Contains("boom", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Defer_runs_on_early_return()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        // Return value should be preserved even with a defer block
        await engine.ExecuteToListAsync(
            "func early() {\n" +
            "  defer { echo cleanup }\n" +
            "  return 42\n" +
            "  echo unreachable\n" +
            "}");
        var results = await engine.ExecuteToListAsync("early");

        Assert.Single(results);
        Assert.Equal(42L, Convert.ToInt64(results[0]));
    }

    [Fact]
    public async Task Defer_multiple_blocks_all_execute()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        // Multiple defers should all execute without error
        await engine.ExecuteToListAsync(
            "func multi() {\n" +
            "  defer { echo first-cleanup }\n" +
            "  defer { echo second-cleanup }\n" +
            "  echo done\n" +
            "}");
        var results = await engine.ExecuteToListAsync("multi");

        // Only "done" reaches the caller — defer output is discarded
        Assert.Single(results);
        Assert.Equal("done", results[0]?.ToString());
    }

    // --- Slice 5: partition, take-until, skip-until, find-index ---

    [Fact]
    public async Task Partition_splits_by_predicate()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("echo 1 2 3 4 5 6 | partition { _ > 3 }");

        Assert.Single(results);
        var pair = (object?[])results[0]!;
        var matches = ((object?[])pair[0]!).Select(x => Convert.ToInt64(x)).ToArray();
        var nonMatches = ((object?[])pair[1]!).Select(x => Convert.ToInt64(x)).ToArray();
        Assert.Equal([4L, 5L, 6L], matches);
        Assert.Equal([1L, 2L, 3L], nonMatches);
    }

    [Fact]
    public async Task Partition_with_callable()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            "echo 10 20 30 40 | partition func(x) { return ($x >= 30) }");

        Assert.Single(results);
        var pair = (object?[])results[0]!;
        var matches = ((object?[])pair[0]!).Select(x => Convert.ToInt64(x)).ToArray();
        var nonMatches = ((object?[])pair[1]!).Select(x => Convert.ToInt64(x)).ToArray();
        Assert.Equal([30L, 40L], matches);
        Assert.Equal([10L, 20L], nonMatches);
    }

    [Fact]
    public async Task TakeUntil_stops_at_predicate()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("echo 1 2 3 4 5 | take-until { _ == 4 }");

        Assert.Equal(3, results.Count);
        Assert.Equal([1L, 2L, 3L], results.Select(x => Convert.ToInt64(x)).ToArray());
    }

    [Fact]
    public async Task TakeUntil_yields_all_if_predicate_never_true()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("echo 1 2 3 | take-until { _ > 100 }");

        Assert.Equal(3, results.Count);
        Assert.Equal([1L, 2L, 3L], results.Select(x => Convert.ToInt64(x)).ToArray());
    }

    [Fact]
    public async Task SkipUntil_skips_before_predicate()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("echo 1 2 3 4 5 | skip-until { _ >= 3 }");

        Assert.Equal(3, results.Count);
        Assert.Equal([3L, 4L, 5L], results.Select(x => Convert.ToInt64(x)).ToArray());
    }

    [Fact]
    public async Task SkipUntil_yields_nothing_if_predicate_never_true()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("echo 1 2 3 | skip-until { _ > 100 }");

        Assert.Empty(results);
    }

    [Fact]
    public async Task FindIndex_returns_first_match()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("echo a b c d | find-index { _ == c }");

        Assert.Single(results);
        Assert.Equal(2, Convert.ToInt32(results[0]));
    }

    [Fact]
    public async Task FindIndex_returns_negative_one_if_no_match()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("echo a b c | find-index { _ == z }");

        Assert.Single(results);
        Assert.Equal(-1, Convert.ToInt32(results[0]));
    }

    // --- Slice 6: unfold, iterate, converge ---

    [Fact]
    public async Task Unfold_generates_values_from_seed()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        // Generate countdown: 5, 4, 3, 2, 1 then stop
        await engine.ExecuteToListAsync(
            "func step(n) {\n" +
            "  if ($n <= 0) { return null }\n" +
            "  return [$n, ($n - 1)]\n" +
            "}");
        var results = await engine.ExecuteToListAsync("unfold 5 &step");

        Assert.Equal(5, results.Count);
        Assert.Equal([5L, 4L, 3L, 2L, 1L], results.Select(x => Convert.ToInt64(x)).ToArray());
    }

    [Fact]
    public async Task Unfold_stops_on_null()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        // Immediately return null → empty output
        await engine.ExecuteToListAsync("func stop(n) { return null }");
        var results = await engine.ExecuteToListAsync("unfold 1 &stop");

        Assert.Empty(results);
    }

    [Fact]
    public async Task Iterate_generates_sequence_with_take()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        // Powers of 2: 1, 2, 4, 8, 16
        await engine.ExecuteToListAsync("func dbl(x) { return ($x * 2) }");
        var results = await engine.ExecuteToListAsync("iterate 1 &dbl | first 5");

        Assert.Equal(5, results.Count);
        Assert.Equal([1L, 2L, 4L, 8L, 16L], results.Select(x => Convert.ToInt64(x)).ToArray());
    }

    [Fact]
    public async Task Iterate_with_take_while()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        // Count up from 1, stop before 6
        await engine.ExecuteToListAsync("func inc(x) { return ($x + 1) }");
        var results = await engine.ExecuteToListAsync("iterate 1 &inc | take-while { _ < 6 }");

        Assert.Equal(5, results.Count);
        Assert.Equal([1L, 2L, 3L, 4L, 5L], results.Select(x => Convert.ToInt64(x)).ToArray());
    }

    [Fact]
    public async Task Converge_finds_fixed_point()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        // Clamp to 0: repeatedly subtract 1, min 0 → converges at 0
        await engine.ExecuteToListAsync(
            "func clamp(x) {\n" +
            "  var next = ($x - 1)\n" +
            "  if ($next < 0) { return 0 }\n" +
            "  return $next\n" +
            "}");
        var results = await engine.ExecuteToListAsync("converge 5 &clamp");

        Assert.Single(results);
        Assert.Equal(0L, Convert.ToInt64(results[0]));
    }

    [Fact]
    public async Task Converge_immediate_fixed_point()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        // Identity function converges immediately
        await engine.ExecuteToListAsync("func id(x) { return $x }");
        var results = await engine.ExecuteToListAsync("converge 42 &id");

        Assert.Single(results);
        Assert.Equal(42L, Convert.ToInt64(results[0]));
    }

    [Fact]
    public async Task Converge_uses_structural_equality_for_arrays()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("converge [1, 2] func(x) => ([1, 2])");

        var result = Assert.Single(results);
        Assert.IsAssignableFrom<Array>(result);
        Assert.Equal([1L, 2L], ((Array)result!).Cast<object?>().Select(Convert.ToInt64).ToArray());
    }

    [Fact]
    public async Task Converge_uses_structural_equality_for_records()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            record Item(name: string, quantity: int, category?: string = "Food")
            func sameRecord(x) { return new Item("Bread", 2, "Food") }
            var result = (converge (new Item("Bread", 2, "Food")) &sameRecord)
            $result.Name
            $result.Quantity
            $result.Category
            """);

        Assert.Equal("Bread", results[0]);
        Assert.Equal(2L, Convert.ToInt64(results[1]));
        Assert.Equal("Food", results[2]);
    }

    [Fact]
    public async Task TupleAssignment_swaps_variables()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync("var a = 1");
        await engine.ExecuteToListAsync("var b = 2");
        await engine.ExecuteToListAsync("($a, $b) = [$b, $a]");
        var aResult = await engine.ExecuteToListAsync("echo $a");
        var bResult = await engine.ExecuteToListAsync("echo $b");
        Assert.Equal(2L, Convert.ToInt64(aResult[0]));
        Assert.Equal(1L, Convert.ToInt64(bResult[0]));
    }

    [Fact]
    public async Task TupleAssignment_handles_extra_and_missing()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync("var x = 10");
        await engine.ExecuteToListAsync("var y = 20");
        await engine.ExecuteToListAsync("var z = 30");
        await engine.ExecuteToListAsync("($x, $y, $z) = [1, 2]");
        var xResult = await engine.ExecuteToListAsync("echo $x");
        var yResult = await engine.ExecuteToListAsync("echo $y");
        var zResult = await engine.ExecuteToListAsync("echo $z");
        Assert.Equal(1L, Convert.ToInt64(xResult[0]));
        Assert.Equal(2L, Convert.ToInt64(yResult[0]));
        object? zVal = zResult.Count > 0 ? zResult[0] : null;
        Assert.Null(zVal);
        await engine.ExecuteToListAsync("($x, $y) = [100, 200, 300]");
        var xResult2 = await engine.ExecuteToListAsync("echo $x");
        var yResult2 = await engine.ExecuteToListAsync("echo $y");
        Assert.Equal(100L, Convert.ToInt64(xResult2[0]));
        Assert.Equal(200L, Convert.ToInt64(yResult2[0]));
    }
}
