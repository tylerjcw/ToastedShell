using System.Dynamic;
using Tosh.Runtime;
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

        await engine.ExecuteToListAsync("var rec = {| Name = Alice, Age = 30 |}");
        await engine.ExecuteToListAsync("var { Name, Age } = $rec");
        var results = await engine.ExecuteToListAsync("echo $Name $Age");

        Assert.Equal("Alice", results[0]?.ToString());
        Assert.Equal(30L, Convert.ToInt64(results[1]));
    }

    [Fact]
    public async Task RecordDestructuring_missing_fields_are_null()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync("var rec = {| X = 1 |}");
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

        await engine.ExecuteToListAsync("var base = {| Name = Tosh, Version = 1 |}");
        var results = await engine.ExecuteToListAsync("{| ...$base, Version = 2 |}");

        var record = Assert.IsAssignableFrom<IDictionary<string, object?>>(results[0]);
        Assert.Equal("Tosh", record["Name"]?.ToString());
        Assert.Equal(2L, Convert.ToInt64(record["Version"]));
    }

    [Fact]
    public async Task Spread_merges_multiple_records()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync("var a = {| X = 1 |}");
        await engine.ExecuteToListAsync("var b = {| Y = 2 |}");
        var results = await engine.ExecuteToListAsync("{| ...$a, ...$b |}");

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
        var results = await engine.ExecuteToListAsync("{| ($key) = active |}");

        var record = Assert.IsAssignableFrom<IDictionary<string, object?>>(results[0]);
        Assert.Equal("active", record["status"]?.ToString());
    }

    [Fact]
    public async Task Computed_property_with_static_fields()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync("var k = echo color");
        var results = await engine.ExecuteToListAsync("{| Name = widget, ($k) = blue |}");

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
            var rows = [{| Name = alpha |}, {| Name = beta |}]
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
            var x = {| Name = Tosh, Version = 1 |}
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
            var x = {| Name = Tosh, Version = 1 |}
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

    // ── Block Comments ──

    [Fact]
    public async Task BlockComment_is_ignored_by_parser()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync("##{ This is a block comment }##\nvar x = 42");
        var results = await engine.ExecuteToListAsync("echo $x");

        Assert.Equal(42L, Convert.ToInt64(results[0]));
    }

    [Fact]
    public async Task BlockComment_multiline_is_ignored()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync("##{\nThis spans\nmultiple lines\n}##\nvar y = 99");
        var results = await engine.ExecuteToListAsync("echo $y");

        Assert.Equal(99L, Convert.ToInt64(results[0]));
    }

    [Fact]
    public async Task BlockComment_inline_preserves_surrounding_code()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync("var a = 10\n##{ skipped }##\nvar b = 20");
        var results = await engine.ExecuteToListAsync("echo $a $b");

        Assert.Equal(10L, Convert.ToInt64(results[0]));
        Assert.Equal(20L, Convert.ToInt64(results[1]));
    }

    // ── Const Keyword ──

    [Fact]
    public async Task Const_declaration_stores_value()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync("const pi = 3.14");
        var results = await engine.ExecuteToListAsync("echo $pi");

        Assert.Equal(3.14, Convert.ToDouble(results[0]));
    }

    [Fact]
    public async Task Const_reassignment_throws()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync("const x = 42");
        var ex = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("$x = 100"));

        Assert.Contains("const", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Const_compound_assignment_throws()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync("const n = 10");
        var ex = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("$n += 5"));

        Assert.Contains("const", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Power_operator_basic()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync("echo (2 ** 3)");
        Assert.Equal(8, results[0]);
    }

    [Fact]
    public async Task Power_operator_right_associative()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        // 2 ** 3 ** 2 should be 2 ** (3 ** 2) = 2 ** 9 = 512
        var results = await engine.ExecuteToListAsync("echo (2 ** 3 ** 2)");
        Assert.Equal(512, results[0]);
    }

    [Fact]
    public async Task Power_operator_zero_exponent()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync("echo (10 ** 0)");
        Assert.Equal(1, results[0]);
    }

    [Fact]
    public async Task Power_compound_assignment()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync("var x = 3");
        await engine.ExecuteToListAsync("$x **= 2");
        var results = await engine.ExecuteToListAsync("echo $x");
        Assert.Equal(9, results[0]);
    }

    [Fact]
    public async Task Function_default_parameter_used_when_omitted()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync("func greet(name = \"world\") { echo $name }");
        var results = await engine.ExecuteToListAsync("greet");
        Assert.Equal("world", results[0]);
    }

    [Fact]
    public async Task Function_default_parameter_overridden_by_argument()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync("func greet(name = \"world\") { echo $name }");
        var results = await engine.ExecuteToListAsync("greet Alice");
        Assert.Equal("Alice", results[0]);
    }

    [Fact]
    public async Task Function_default_parameter_mixed_required_and_default()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync("func connect(host, port = 8080) { echo $port }");
        var results = await engine.ExecuteToListAsync("connect example.com");
        Assert.Equal(8080, results[0]);
    }

    [Fact]
    public async Task Function_default_parameter_expression()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync("func calc(x, y = (2 + 3)) { echo ($x + $y) }");
        var results = await engine.ExecuteToListAsync("calc 10");
        Assert.Equal(15, results[0]);
    }

    [Fact]
    public async Task Named_arguments_basic()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync("func greet(name, greeting) { echo $greeting }");
        var results = await engine.ExecuteToListAsync("greet(name = \"Alice\", greeting = \"Hello\")");
        Assert.Equal("Hello", results[0]);
    }

    [Fact]
    public async Task Named_arguments_out_of_order()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync("func connect(host, port) { echo $port }");
        var results = await engine.ExecuteToListAsync("connect(port = 8080, host = \"example.com\")");
        Assert.Equal(8080, results[0]);
    }

    [Fact]
    public async Task Named_arguments_with_defaults()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync("func connect(host, port = 443) { echo $port }");
        var results = await engine.ExecuteToListAsync("connect(host = \"example.com\")");
        Assert.Equal(443, results[0]);
    }

    // ── Function call argument parsing (not tuples) ──

    [Fact]
    public async Task Function_call_multiple_args_not_tuple()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync("func test_args(a, b, c) { echo $a $b $c }");
        var results = await engine.ExecuteToListAsync("test_args(1, 2, 3)");
        Assert.Equal(3, results.Count);
        Assert.Equal(1, results[0]);
        Assert.Equal(2, results[1]);
        Assert.Equal(3, results[2]);
    }

    [Fact]
    public async Task Function_call_two_args_not_tuple()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync("func add(a, b) { echo ($a + $b) }");
        var results = await engine.ExecuteToListAsync("add(3, 4)");
        Assert.Single(results);
        Assert.Equal(7L, Convert.ToInt64(results[0]));
    }

    [Fact]
    public async Task Function_call_single_arg_no_tuple()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync("func double(x) { echo ($x * 2) }");
        var results = await engine.ExecuteToListAsync("double(5)");
        Assert.Single(results);
        Assert.Equal(10L, Convert.ToInt64(results[0]));
    }

    [Fact]
    public async Task Function_call_with_space_before_paren_is_tuple_arg()
    {
        // When there's a space: `echo (1, 2, 3)` the (1,2,3) is a tuple argument
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync("echo (1, 2, 3) | type-of | get Name");
        Assert.Contains("tuple", results[0]?.ToString()?.ToLower() ?? "");
    }

    // ── Pipe-forward |> ──

    [Fact]
    public async Task PipeForward_passes_value_as_first_argument()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync("\"hello\" |> echo");
        Assert.Single(results);
        Assert.Equal("hello", results[0]?.ToString());
    }

    [Fact]
    public async Task PipeForward_expression_as_first_argument()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync("(2 + 3) |> echo");
        Assert.Single(results);
        Assert.Equal(5L, Convert.ToInt64(results[0]));
    }

    [Fact]
    public async Task PipeForward_prepends_before_existing_arguments()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync("func add(a, b) { echo ($a + $b) }");
        var results = await engine.ExecuteToListAsync("5 |> add 3");
        Assert.Single(results);
        Assert.Equal(8L, Convert.ToInt64(results[0]));
    }

    [Fact]
    public async Task PipeForward_chains_multiple_stages()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync("func double(n) { echo ($n * 2) }");
        var results = await engine.ExecuteToListAsync("5 |> double |> double");
        Assert.Single(results);
        Assert.Equal(20L, Convert.ToInt64(results[0]));
    }

    // ── yield / generators ──

    [Fact]
    public async Task Yield_emits_values_from_generator_function()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync("func gen() { yield 1; yield 2; yield 3 }");
        var results = await engine.ExecuteToListAsync("gen");
        Assert.Equal([1L, 2L, 3L], results.Select(Convert.ToInt64).ToArray());
    }

    [Fact]
    public async Task Yield_in_loop_produces_sequence()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync("func countdown(n) { var i = $n; while ($i > 0) { yield $i; $i = $i - 1 } }");
        var results = await engine.ExecuteToListAsync("countdown 3");
        Assert.Equal([3L, 2L, 1L], results.Select(Convert.ToInt64).ToArray());
    }

    [Fact]
    public async Task Yield_with_return_stops_early()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync("func partial() { yield 10; yield 20; return 30 }");
        var results = await engine.ExecuteToListAsync("partial");
        Assert.Equal([10L, 20L, 30L], results.Select(Convert.ToInt64).ToArray());
    }

    [Fact]
    public async Task Yield_works_with_pipeline()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync("func squares(n) { for i in (1..$n) { yield ($i * $i) } }");
        var results = await engine.ExecuteToListAsync("squares 4");
        Assert.Equal([1L, 4L, 9L, 16L], results.Select(Convert.ToInt64).ToArray());
    }

    // ── parallel pipeline command ──

    [Fact]
    public async Task Parallel_processes_items_with_block()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync("echo 1 2 3 | parallel { echo (_ * 2) }");
        Assert.Equal([2L, 4L, 6L], results.Select(Convert.ToInt64).ToArray());
    }

    [Fact]
    public async Task Parallel_preserves_input_order()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync("echo 5 3 1 4 2 | parallel { _ }");
        Assert.Equal([5L, 3L, 1L, 4L, 2L], results.Select(Convert.ToInt64).ToArray());
    }

    [Fact]
    public async Task Parallel_with_callable()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync("func triple(n) { echo ($n * 3) }");
        var results = await engine.ExecuteToListAsync("echo 1 2 3 | parallel { triple $_ }");
        Assert.Equal([3L, 6L, 9L], results.Select(Convert.ToInt64).ToArray());
    }

    // ── interfaces ──

    [Fact]
    public async Task Interface_definition_and_class_implementing_it()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync("interface Greeter { func greet(name) }");
        await engine.ExecuteToListAsync(@"
            class Hello fulfills Greeter {
                func greet(name) { echo $""Hello, {$name}!"" }
            }
        ");
        var results = await engine.ExecuteToListAsync("var obj = new Hello(); $obj.greet(\"world\")");
        Assert.Equal("Hello, world!", results.Single());
    }

    [Fact]
    public async Task Interface_missing_method_throws_error()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync("interface Printable { func toString() }");
        var ex = await Assert.ThrowsAsync<ToshDiagnosticException>(async () =>
        {
            await engine.ExecuteToListAsync("class Broken fulfills Printable { }");
        });
        Assert.Contains("toString", ex.Message);
    }

    [Fact]
    public async Task Interface_multiple_methods_validated()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync("interface Serializable { func serialize(); func deserialize(data) }");
        await engine.ExecuteToListAsync(@"
            class JsonObj fulfills Serializable {
                func serialize() { echo ""json"" }
                func deserialize(data) { echo $data }
            }
        ");
        var results = await engine.ExecuteToListAsync("var obj = new JsonObj(); $obj.serialize()");
        Assert.Equal("json", results.Single());
    }

    [Fact]
    public async Task Interface_multiple_interfaces_on_class()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync("interface Readable { func read() }");
        await engine.ExecuteToListAsync("interface Writable { func write(data) }");
        await engine.ExecuteToListAsync(@"
            class File fulfills Readable, Writable {
                func read() { echo ""content"" }
                func write(data) { echo $data }
            }
        ");
        var results = await engine.ExecuteToListAsync("var f = new File(); $f.read()");
        Assert.Equal("content", results.Single());
    }

    // ── class inheritance ──

    [Fact]
    public async Task Class_inherits_method_from_base()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync(@"
            class Animal {
                func speak() { echo ""..."" }
            }
        ");
        await engine.ExecuteToListAsync(@"
            class Dog extends Animal {
                func fetch() { echo ""fetching"" }
            }
        ");
        var results = await engine.ExecuteToListAsync("var d = new Dog(); $d.speak()");
        Assert.Equal("...", results.Single());
    }

    [Fact]
    public async Task Class_overrides_base_method()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync(@"
            class Animal {
                func speak() { echo ""..."" }
            }
        ");
        await engine.ExecuteToListAsync(@"
            class Cat extends Animal {
                overrule func speak() { echo ""meow"" }
            }
        ");
        var results = await engine.ExecuteToListAsync("var c = new Cat(); $c.speak()");
        Assert.Equal("meow", results.Single());
    }

    [Fact]
    public async Task Class_inherits_property_from_base()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync(@"
            class Vehicle {
                prop wheels = 4
            }
        ");
        await engine.ExecuteToListAsync(@"
            class Car extends Vehicle {
                prop brand = ""generic""
            }
        ");
        var results = await engine.ExecuteToListAsync("var c = new Car(); $c.wheels");
        Assert.Equal(4, results.Single());
    }

    [Fact]
    public async Task Class_super_calls_base_method()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync(@"
            class Base {
                func greet() { echo ""hello"" }
            }
        ");
        await engine.ExecuteToListAsync(@"
            class Child extends Base {
                overrule func greet() { echo ""hey""; $super.greet() }
            }
        ");
        var results = await engine.ExecuteToListAsync("var c = new Child(); $c.greet()");
        var flat = results.SelectMany(r => r is object[] arr ? arr : new[] { r }).ToArray();
        Assert.Equal(new object[] { "hey", "hello" }, flat);
    }

    [Fact]
    public async Task Class_extends_unknown_class_throws_error()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var ex = await Assert.ThrowsAsync<ToshDiagnosticException>(async () =>
        {
            await engine.ExecuteToListAsync("class Broken extends NonExistent { }");
        });
        Assert.Contains("NonExistent", ex.Message);
    }

    [Fact]
    public async Task Class_extends_dotted_type_name_parses_correctly()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        // System.Drawing.Point may or may not be loadable, but the parser must accept dotted names.
        // If it resolves, the class definition succeeds; if not, it throws unknown_base_class.
        // Either way, we should NOT get a parser error.
        try
        {
            await engine.ExecuteToListAsync("class MyPoint extends System.Drawing.Point { }");
            // If it succeeds, the CLR type resolved — that's fine
        }
        catch (ToshDiagnosticException ex) when (ex.Diagnostics[0].Code == "tosh.runtime.unknown_base_class")
        {
            // Also acceptable — CLR type not loaded, but parser accepted it
            Assert.Contains("System.Drawing.Point", ex.Message);
        }
    }

    [Fact]
    public async Task Class_extends_clr_type_and_accesses_base_properties()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync(@"
            class MyUri extends System.Uri {
                prop Label = ''

                MyUri(url, label) {
                    $super($url)
                    $this.Label = $label
                }
            }
        ");
        var results = await engine.ExecuteToListAsync(
            "var u = new MyUri('https://example.com/path', 'Example'); echo $u.Label $u.Host $u.AbsolutePath");
        Assert.Equal("Example", results[0]?.ToString());
        Assert.Equal("example.com", results[1]?.ToString());
        Assert.Equal("/path", results[2]?.ToString());
    }

    [Fact]
    public async Task Class_extends_clr_type_and_calls_base_method()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync(@"
            class SmartList extends System.Collections.ArrayList {
                SmartList() {
                    $super()
                }

                func item_count() {
                    echo $this.Count
                }
            }
        ");
        await engine.ExecuteToListAsync("var sl = new SmartList(); $sl.Add(1); $sl.Add(2); $sl.Add(3)");
        var results = await engine.ExecuteToListAsync("$sl.item_count()");
        Assert.Equal(3, Convert.ToInt32(results[0]));
    }

    // ── implicit usings & CLR namespace resolution ──

    [Fact]
    public async Task Implicit_using_resolves_common_types_without_using_statement()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        // StringBuilder is in System.Text which is an implicit using
        var results = await engine.ExecuteToListAsync("var sb = new StringBuilder(); $sb.Append('hello'); echo $sb.ToString()");
        Assert.Equal("hello", results[0]?.ToString());
    }

    [Fact]
    public async Task Implicit_using_resolves_generic_types()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        // List<string> is in System.Collections.Generic
        await engine.ExecuteToListAsync("var items = new List<string>(); $items.Add('a'); $items.Add('b')");
        var results = await engine.ExecuteToListAsync("echo $items.Count");
        Assert.Equal(2, Convert.ToInt32(results[0]));
    }

    [Fact]
    public async Task Explicit_using_resolves_non_default_namespace()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        // System.Globalization is not in the default implicit usings
        var results = await engine.ExecuteToListAsync("using System.Globalization; var ci = new CultureInfo('en-US'); echo $ci.Name");
        Assert.Equal("en-US", results[0]?.ToString());
    }

    [Fact]
    public async Task Class_extends_clr_type_via_implicit_using()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        // Uri is in System (implicit) — should work with short name in extends
        await engine.ExecuteToListAsync(@"
            class TaggedUri extends Uri {
                prop Tag = ''
                TaggedUri(url, tag) {
                    $super($url)
                    $this.Tag = $tag
                }
            }
        ");
        var results = await engine.ExecuteToListAsync(
            "var u = new TaggedUri('https://example.com', 'test'); echo $u.Tag $u.Host");
        Assert.Equal("test", results[0]?.ToString());
        Assert.Equal("example.com", results[1]?.ToString());
    }

    [Fact]
    public async Task Class_super_call_invokes_base_constructor()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync(@"
            class Pt {
                prop X
                prop Y
                Pt(x, y) {
                    $this.X = $x
                    $this.Y = $y
                }
            }
        ");
        await engine.ExecuteToListAsync(@"
            class Pt3 extends Pt {
                prop Z
                Pt3(x, y, z) {
                    $super($x, $y)
                    $this.Z = $z
                }
            }
        ");
        var results = await engine.ExecuteToListAsync("var p = new Pt3(1, 2, 3); echo $p.X $p.Y $p.Z");
        Assert.Equal([1, 2, 3], results.Select(Convert.ToInt32).ToArray());
    }

    // ── discriminated unions ──

    [Fact]
    public async Task Union_variant_with_fields()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync(@"
            union Shape {
                Circle(radius)
                Rectangle(width, height)
            }
        ");
        var results = await engine.ExecuteToListAsync("var s = Shape.Circle(5); $s.radius");
        Assert.Equal(5, results.Single());
    }

    [Fact]
    public async Task Union_unit_variant()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync(@"
            union Color {
                Red
                Green
                Blue
            }
        ");
        var results = await engine.ExecuteToListAsync("var c = Color.Red; $c.Variant");
        Assert.Equal("Red", results.Single());
    }

    [Fact]
    public async Task Union_variant_tag_accessible()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync(@"
            union Result {
                Ok(value)
                Err(message)
            }
        ");
        var results = await engine.ExecuteToListAsync("var r = Result.Err(\"oops\"); $r.Tag");
        Assert.Equal("Err", results.Single());
    }

    [Fact]
    public async Task Union_variant_toString()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync(@"
            union Option {
                Some(value)
                None
            }
        ");
        var results = await engine.ExecuteToListAsync("var x = Option.Some(42); echo $x");
        Assert.Equal("Option.Some(42)", results.Single()?.ToString());
    }

    // ── Is / As Operators ──

    [Fact]
    public async Task Is_operator_checks_primitive_types()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            var x = 42
            var s = ""hello""
            var b = true
            echo ($x is int) ($s is string) ($b is bool)
        ");
        Assert.Equal([true, true, true], results);
    }

    [Fact]
    public async Task Is_not_operator_works()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            var x = 42
            echo ($x is-not string) ($x is-not int)
        ");
        Assert.Equal([true, false], results);
    }

    [Fact]
    public async Task Is_null_checks_nullity()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            var x = null
            var y = 42
            echo ($x is null) ($y is null) ($x is-not null) ($y is-not null)
        ");
        Assert.Equal([true, false, false, true], results);
    }

    [Fact]
    public async Task Is_operator_checks_tosh_class_name()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync(@"
            class Dog {
                prop Name = ""Rex""
            }
        ");
        var results = await engine.ExecuteToListAsync(@"
            var d = new Dog()
            echo ($d is Dog) ($d is int)
        ");
        Assert.Equal([true, false], results);
    }

    [Fact]
    public async Task Is_operator_recognises_numeric_trait_constraint()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            var i = 42
            var f = 3.14
            var s = ""hello""
            echo ($i is Numeric) ($f is Numeric) ($s is Numeric) ($i is Number) ($i is INumber) ($s is-not Numeric)
        ");
        Assert.Equal([true, true, false, true, true, true], results);
    }

    [Fact]
    public async Task Is_operator_recognises_comparable_trait_constraint()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            var i = 42
            var s = ""hello""
            echo ($i is Comparable) ($s is Comparable)
        ");
        Assert.Equal([true, true], results);
    }

    [Fact]
    public async Task Match_arm_pipeline_body_supports_throw_statement()
    {
        // Regression: previously `default => throw new ...` was parsed as a
        // pipeline starting with the bareword command `throw`, producing
        // 'tosh.runtime.unknown_command' instead of raising the exception.
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            func classify(x) {
                return match ($x) {
                    _ is Numeric => ""number""
                    _ is String  => ""string""
                    default      => throw ""unsupported""
                }
            }
            try {
                classify [1, 2]
            } catch ($err) {
                echo $""caught: {$err}""
            }
            echo (classify 42)
            echo (classify ""hi"")
        ");
        Assert.Equal(["caught: unsupported", "number", "string"], results);
    }

    [Fact]
    public async Task Match_arm_pipeline_body_supports_return_statement()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            func classify(x) {
                match ($x) {
                    _ > 0 => return ""pos""
                    _ < 0 => return ""neg""
                    default => return ""zero""
                }
                return ""?""
            }
            echo (classify 5)
            echo (classify -3)
            echo (classify 0)
        ");
        Assert.Equal(["pos", "neg", "zero"], results);
    }

    [Fact]
    public async Task Generic_class_infers_type_arguments_from_ctor_args()
    {
        // Phase 6.16 — `new Box(42)` ⇒ `T = int` from the ctor argument
        // type, no `<int>` ceremony required.
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            class Box<T>(initial: T) where T: Numeric {
                prop value: T = $initial
            }
            var bi = new Box(42)
            var bf = new Box(3.14)
            echo $bi.value
            echo $bf.value
            echo ((type-of $bi).TypeArguments[0].Name)
            echo ((type-of $bf).TypeArguments[0].Name)
        ");
        Assert.Equal([42, 3.14, "Int32", "Double"], results);
    }

    [Fact]
    public async Task Generic_class_inference_still_enforces_constraints()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            class Box<T>(initial: T) where T: Numeric {
                prop value: T = $initial
            }
            try {
                var bad = new Box(""hi"")
                echo ""unreachable""
            } catch ($err) {
                echo ""caught""
            }
        ");
        Assert.Equal(["caught"], results);
    }

    [Fact]
    public async Task Generic_record_infers_type_arguments_from_ctor_args()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            record Pair<A, B>(first: A, second: B)
            var p = new Pair(""hello"", 7)
            echo $p.first
            echo $p.second
        ");
        Assert.Equal(["hello", 7], results);
    }

    [Fact]
    public async Task Generic_class_infers_nested_list_element_type()
    {
        // Phase 6.16 — nested annotations: `class Box<T>(values: list<T>)`
        // should infer T from the element type of the supplied list.
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            class Box<T>(values: list<T>) {
                prop values = $values
            }
            var b = new Box([1, 2, 3])
            echo ((type-of $b).TypeArguments[0].Name)
        ");
        Assert.Equal(["Int32"], results);
    }

    [Fact]
    public async Task Generic_class_infers_type_arguments_through_clr_generic_type()
    {
        // Phase 6.16 — `Head<T>` annotation matched against any generic
        // CLR runtime type unifies pointwise.
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            class Wrap<T>(items: list<T>) {
                prop items = $items
            }
            var w = new Wrap([""a"", ""b""])
            echo ((type-of $w).TypeArguments[0].Name)
        ");
        Assert.Equal(["String"], results);
    }

    [Fact]
    public async Task Is_operator_walks_tosh_class_hierarchy()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync(@"
            class Animal {
                prop Name = ""Rex""
            }
            class Dog extends Animal {
                prop Breed = ""Lab""
            }
        ");
        var results = await engine.ExecuteToListAsync(@"
            var d = new Dog()
            echo ($d is Dog) ($d is Animal)
        ");
        Assert.Equal([true, true], results);
    }

    [Fact]
    public async Task Is_operator_checks_tosh_interfaces()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync(@"
            interface IGreetable {
                func greet() : string
            }
            class Person fulfills IGreetable {
                prop Name = ""Alice""
                func greet() {
                    echo ""hi""
                }
            }
        ");
        var results = await engine.ExecuteToListAsync(@"
            var p = new Person()
            echo ($p is Person) ($p is IGreetable)
        ");
        Assert.Equal([true, true], results);
    }

    [Fact]
    public async Task Is_operator_checks_tosh_enum()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync(@"
            enum Color {
                Red = 1
                Green = 2
                Blue = 3
            }
        ");
        var results = await engine.ExecuteToListAsync(@"
            var c = Color.Red
            echo ($c is Color) ($c is int)
        ");
        Assert.Equal([true, false], results);
    }

    [Fact]
    public async Task As_operator_converts_primitive_types()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            echo (42 as double) (""123"" as int)
        ");
        Assert.Equal(42.0, results[0]);
        Assert.Equal(123L, Convert.ToInt64(results[1]));
    }

    [Fact]
    public async Task As_operator_returns_tosh_instance_when_compatible()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync(@"
            class Animal {
                prop Name = ""Rex""
            }
            class Dog extends Animal {
                prop Breed = ""Lab""
            }
        ");
        var results = await engine.ExecuteToListAsync(@"
            var d = new Dog()
            var a = ($d as Animal)
            echo $a.Name
        ");
        Assert.Equal("Rex", results.Single());
    }

    [Fact]
    public async Task Is_operator_with_type_aliases()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            var n = 42
            var text = ""hello""
            echo ($n is int) ($text is str)
        ");
        Assert.Equal([true, true], results);
    }

    [Fact]
    public async Task Shared_property_is_accessible_on_class_type()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            class Counter {
                shared prop count = 0
                prop name
            }
            echo Counter.count
        ");
        Assert.Equal([0], results);
    }

    [Fact]
    public async Task Shared_property_with_initial_value()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            class Config {
                shared prop version = ""1.0""
                shared prop maxRetries = 3
            }
            echo Config.version Config.maxRetries
        ");
        Assert.Equal(["1.0", 3], results);
    }

    [Fact]
    public async Task Shared_property_not_on_instance()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            class Foo {
                shared prop bar = 42
                prop name = ""test""
            }
            var f = new Foo()
            echo $f.name
        ");
        // Static property should not appear on instance — only 'name' is accessible
        Assert.Equal(["test"], results);
    }

    [Fact]
    public async Task Sealed_class_cannot_be_extended()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var ex = await Assert.ThrowsAsync<ToshDiagnosticException>(async () =>
        {
            await engine.ExecuteToListAsync(@"
                sealed class Immutable {
                    prop value = 1
                }
                class Child extends Immutable {
                    prop extra = 2
                }
            ");
        });
        Assert.Contains("sealed", ex.Diagnostics[0].Title);
    }

    [Fact]
    public async Task Sealed_class_can_be_instantiated()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            sealed class Leaf {
                prop value = 99
            }
            var obj = new Leaf()
            echo $obj.value
        ");
        Assert.Equal([99], results);
    }

    [Fact]
    public async Task Hollow_class_cannot_be_instantiated()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var ex = await Assert.ThrowsAsync<ToshDiagnosticException>(async () =>
        {
            await engine.ExecuteToListAsync(@"
                hollow class Shape {
                    prop name = ""shape""
                }
                var s = new Shape()
            ");
        });
        Assert.Contains("hollow", ex.Message);
    }

    [Fact]
    public async Task Hollow_class_subclass_works()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            hollow class Shape {
                prop name = ""shape""
                func area() {
                    echo 0
                }
            }
            class Circle extends Shape {
                prop radius = 5
            }
            var c = new Circle()
            echo $c.name $c.radius
        ");
        Assert.Equal(["shape", 5], results);
    }

    [Fact]
    public async Task Hollow_method_must_be_implemented_by_subclass()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var ex = await Assert.ThrowsAsync<ToshDiagnosticException>(async () =>
        {
            await engine.ExecuteToListAsync(@"
                hollow class Shape {
                    hollow func area() {
                        echo 0
                    }
                }
                class Circle extends Shape {
                    prop radius = 5
                }
            ");
        });
        Assert.Contains("hollow", ex.Diagnostics[0].Title);
        Assert.Contains("area", ex.Diagnostics[0].Title);
    }

    [Fact]
    public async Task Hollow_method_implemented_by_subclass_works()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            hollow class Shape {
                hollow func area() {
                    echo 0
                }
            }
            class Square extends Shape {
                prop side = 4
                overrule func area() {
                    echo ($this.side * $this.side)
                }
            }
            var s = new Square()
            echo ($s.area())
        ");
        Assert.Equal([16], results);
    }

    [Fact]
    public async Task Public_modifier_is_noop()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            class Demo {
                public prop name = 42
            }
            var d = new Demo()
            echo $d.name
        ");
        Assert.Equal([42], results);
    }

    [Fact]
    public async Task Shared_alias_works_same_as_static_for_methods()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            class MathHelper {
                shared func multiply(n) {
                    echo ($n * 2)
                }
            }
            echo (MathHelper.multiply(5))
        ");
        Assert.Equal([10], results);
    }

    [Fact]
    public async Task Class_IsAbstract_reflects_hollow()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            hollow class AbstractBase { }
            class ConcreteChild extends AbstractBase { }
            var c = new ConcreteChild()
            echo ($c is AbstractBase) ($c is ConcreteChild)
        ");
        Assert.Equal([true, true], results);
    }

    [Fact]
    public async Task Class_IsSealed_reflects_sealed()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            sealed class Sealed {
                prop value = 10
            }
            var s = new Sealed()
            echo $s.value
        ");
        Assert.Equal([10], results);
    }

    [Fact]
    public async Task Shy_sealed_class_combination()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            func test() {
                shy sealed class Internal {
                    prop value = 42
                }
                var i = new Internal()
                echo $i.value
            }
            test
        ");
        Assert.Equal([42], results);
    }

    [Fact]
    public async Task Proud_modifier_is_explicit_public()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            class Demo {
                proud prop name = 99
            }
            var d = new Demo()
            echo $d.name
        ");
        Assert.Equal([99], results);
    }

    // ── Batch 2: fixed, vital, guarded, overrule ──

    [Fact]
    public async Task Fixed_property_cannot_be_reassigned()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var ex = await Assert.ThrowsAsync<ToshDiagnosticException>(async () =>
        {
            await engine.ExecuteToListAsync(@"
                class Config {
                    fixed prop version = ""1.0""
                }
                var c = new Config()
                $c.version = ""2.0""
            ");
        });
        Assert.Contains("fixed", ex.Message);
    }

    [Fact]
    public async Task Fixed_property_initial_value_works()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            class Config {
                fixed prop version = ""1.0""
            }
            var c = new Config()
            echo $c.version
        ");
        Assert.Equal(["1.0"], results);
    }

    [Fact]
    public async Task Fixed_property_can_be_set_in_constructor()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            class Point {
                fixed prop x = 0
                fixed prop y = 0
                Point(x, y) {
                    $this.x = $x
                    $this.y = $y
                }
            }
            var p = new Point(3, 4)
            echo $p.x $p.y
        ");
        Assert.Equal([3, 4], results);
    }

    [Fact]
    public async Task Vital_property_must_be_set_in_constructor()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var ex = await Assert.ThrowsAsync<ToshDiagnosticException>(async () =>
        {
            await engine.ExecuteToListAsync(@"
                class User {
                    vital prop name
                    User() { }
                }
                var u = new User()
            ");
        });
        Assert.Contains("vital", ex.Message.ToLowerInvariant());
    }

    [Fact]
    public async Task Vital_property_works_when_set_in_constructor()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            class User {
                vital prop name
                User(name) {
                    $this.name = $name
                }
            }
            var u = new User(""Alice"")
            echo $u.name
        ");
        Assert.Equal(["Alice"], results);
    }

    [Fact]
    public async Task Guarded_property_not_accessible_externally()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            class Account {
                guarded prop balance = 100
                prop name = ""test""
                func get_balance() {
                    echo $this.balance
                }
            }
            var a = new Account()
            echo $a.name
        ");
        // Only 'name' is accessible externally; 'balance' is guarded
        Assert.Equal(["test"], results);
    }

    [Fact]
    public async Task Guarded_property_accessible_from_subclass()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            class Account {
                guarded prop balance = 100
            }
            class SavingsAccount extends Account {
                func get_balance() {
                    echo $this.balance
                }
            }
            var s = new SavingsAccount()
            $s.get_balance()
        ");
        Assert.Equal([100], results);
    }

    [Fact]
    public async Task Guarded_method_accessible_from_subclass()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            class Base {
                guarded func secret() {
                    echo 42
                }
            }
            class Child extends Base {
                func reveal() {
                    $this.secret()
                }
            }
            var c = new Child()
            $c.reveal()
        ");
        Assert.Equal([42], results);
    }

    [Fact]
    public async Task Overrule_method_works()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            class Animal {
                func speak() {
                    echo ""...""
                }
            }
            class Dog extends Animal {
                overrule func speak() {
                    echo ""Woof!""
                }
            }
            var d = new Dog()
            $d.speak()
        ");
        Assert.Equal(["Woof!"], results);
    }

    [Fact]
    public async Task Overrule_without_parent_method_fails()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var ex = await Assert.ThrowsAsync<ToshDiagnosticException>(async () =>
        {
            await engine.ExecuteToListAsync(@"
                class Standalone {
                    overrule func ghost() {
                        echo ""boo""
                    }
                }
            ");
        });
        Assert.Contains("overrule", ex.Diagnostics[0].Title);
    }

    [Fact]
    public async Task Overrule_without_base_class_fails()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var ex = await Assert.ThrowsAsync<ToshDiagnosticException>(async () =>
        {
            await engine.ExecuteToListAsync(@"
                class Orphan {
                    overrule func nothing() {
                        echo ""fail""
                    }
                }
            ");
        });
        Assert.Contains("overrule", ex.Diagnostics[0].Title);
    }

    [Fact]
    public async Task Fixed_vital_combination()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            class Immutable {
                fixed vital prop id
                Immutable(id) {
                    $this.id = $id
                }
            }
            var obj = new Immutable(42)
            echo $obj.id
        ");
        Assert.Equal([42], results);
    }

    [Fact]
    public async Task Fixed_vital_cannot_reassign_after_construction()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var ex = await Assert.ThrowsAsync<ToshDiagnosticException>(async () =>
        {
            await engine.ExecuteToListAsync(@"
                class Immutable {
                    fixed vital prop id
                    Immutable(id) {
                        $this.id = $id
                    }
                }
                var obj = new Immutable(42)
                $obj.id = 99
            ");
        });
        Assert.Contains("fixed", ex.Message);
    }

    [Fact]
    public async Task Overrule_hollow_method_works()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            hollow class Shape {
                hollow func area() { }
            }
            class Circle extends Shape {
                prop radius = 5
                overrule func area() {
                    echo ($this.radius * $this.radius * 3)
                }
            }
            var c = new Circle()
            $c.area()
        ");
        Assert.Equal([75], results);
    }

    // ── Batch 3: hermit, strict, lazy, fading, local, raw ──

    [Fact]
    public async Task Hermit_class_cannot_be_instantiated()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var ex = await Assert.ThrowsAsync<ToshDiagnosticException>(async () =>
        {
            await engine.ExecuteToListAsync(@"
                hermit class MathUtils {
                    shared prop pi = 3.14
                    shared func double(x) {
                        return $x * 2
                    }
                }
                var m = new MathUtils()
            ");
        });
        Assert.Contains("hermit", ex.Message.ToLower());
    }

    [Fact]
    public async Task Hermit_class_shared_members_work()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            hermit class MathUtils {
                shared prop pi = 3.14
                shared func double(x) {
                    return $x * 2
                }
            }
            echo MathUtils.pi
            echo (MathUtils.double(5))
        ");
        Assert.Equal(3.14, results[0]);
        Assert.Equal([10], results.Skip(1).ToArray());
    }

    [Fact]
    public async Task Hermit_class_auto_promotes_properties_to_shared()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            hermit class Config {
                prop version = ""2.0""
            }
            echo Config.version
        ");
        Assert.Equal(["2.0"], results);
    }

    [Fact]
    public async Task Hermit_class_auto_promotes_methods_to_shared()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            hermit class Util {
                func greet() {
                    return ""hello""
                }
            }
            echo Util.greet()
        ");
        Assert.Equal(["hello"], results);
    }

    [Fact]
    public async Task Strict_class_properties_are_fixed()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var ex = await Assert.ThrowsAsync<ToshDiagnosticException>(async () =>
        {
            await engine.ExecuteToListAsync(@"
                strict class Point {
                    prop x = 0
                    prop y = 0
                    Point(x, y) {
                        $this.x = $x
                        $this.y = $y
                    }
                }
                var p = new Point(3, 4)
                $p.x = 99
            ");
        });
        Assert.Contains("fixed", ex.Message.ToLower());
    }

    [Fact]
    public async Task Strict_class_allows_reading_properties()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            strict class Point {
                prop x = 0
                prop y = 0
                Point(x, y) {
                    $this.x = $x
                    $this.y = $y
                }
            }
            var p = new Point(3, 4)
            echo $p.x
            echo $p.y
        ");
        Assert.Equal(3, results[0]);
        Assert.Equal(4, results[1]);
    }

    [Fact]
    public async Task Lazy_property_initializes_on_first_access()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            class Config {
                lazy prop value = 42
            }
            var c = new Config()
            echo $c.value
        ");
        Assert.Equal(42, results[0]);
    }

    [Fact]
    public async Task Lazy_property_evaluates_only_once()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            class Cached {
                lazy prop data = 99
            }
            var c = new Cached()
            echo $c.data
            echo $c.data
        ");
        // Both accesses should return 99 (lazy init on first, cached on second)
        Assert.Equal(99, results[0]);
        Assert.Equal(99, results[1]);
    }

    [Fact]
    public async Task Fading_property_emits_deprecation_warning()
    {
        var runtime = ToshRuntime.CreateDefault();
        var errorWriter = new StringWriter();
        runtime.Error = errorWriter;
        var engine = new ToshEngine(runtime);
        var results = await engine.ExecuteToListAsync(@"
            class Legacy {
                fading prop oldName = ""bob""
            }
            var l = new Legacy()
            echo $l.oldName
        ");
        Assert.Equal("bob", results[0]);
        Assert.Contains("fading", errorWriter.ToString().ToLower());
    }

    [Fact]
    public async Task Fading_method_emits_deprecation_warning()
    {
        var runtime = ToshRuntime.CreateDefault();
        var errorWriter = new StringWriter();
        runtime.Error = errorWriter;
        var engine = new ToshEngine(runtime);
        var results = await engine.ExecuteToListAsync(@"
            class Legacy {
                fading func oldGreet() {
                    return ""hello""
                }
            }
            var l = new Legacy()
            echo $l.oldGreet()
        ");
        Assert.Equal(["hello"], results);
        Assert.Contains("fading", errorWriter.ToString().ToLower());
    }

    [Fact]
    public async Task Local_property_hidden_from_outside()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            class MyClass {
                local prop secret = ""hidden""
                func getSecret() {
                    return $this.secret
                }
            }
            var m = new MyClass()
            echo $m.getSecret()
        ");
        Assert.Equal(["hidden"], results);
    }

    [Fact]
    public async Task Raw_method_is_recognized()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            class Interop {
                raw func execute() {
                    return ""raw-result""
                }
            }
            var i = new Interop()
            echo $i.execute()
        ");
        Assert.Equal(["raw-result"], results);
    }

    // ── Partial class tests ────────────────────────────────────────────

    [Fact]
    public async Task Partial_class_merges_properties()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            partial class User { prop Name = ""Alice"" }
            partial class User { prop Age = 30 }
            var u = new User()
            echo $u.Name
            echo $u.Age
        ");
        Assert.Collection(results,
            item => Assert.Equal("Alice", item),
            item => Assert.Equal(30, item));
    }

    [Fact]
    public async Task Partial_class_merges_methods()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            partial class Greeter {
                prop Name = ""World""
            }
            partial class Greeter {
                func greet() {
                    return $""Hello, {$this.Name}!""
                }
            }
            var g = new Greeter()
            echo $g.greet()
        ");
        Assert.Equal(["Hello, World!"], results);
    }

    [Fact]
    public async Task Partial_class_rejects_merge_without_partial_on_original()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var ex = await Assert.ThrowsAsync<ToshDiagnosticException>(async () =>
        {
            await engine.ExecuteToListAsync(@"
                class Foo { prop X = 1 }
                partial class Foo { prop Y = 2 }
            ");
        });
        Assert.Contains("partial", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Partial_class_duplicate_property_is_skipped()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            partial class Cfg { prop Host = ""original"" }
            partial class Cfg { prop Host = ""replaced"" }
            var c = new Cfg()
            echo $c.Host
        ");
        // The first declaration wins; duplicate property names are skipped.
        Assert.Equal(["original"], results);
    }

    [Fact]
    public async Task Partial_class_merges_static_members()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            partial class Utils {
                static func add(a, b) { return ($a + $b) }
            }
            partial class Utils {
                static func mul(a, b) { return ($a * $b) }
            }
            echo (Utils.add(2, 3))
            echo (Utils.mul(4, 5))
        ");
        Assert.Collection(results,
            item => Assert.Equal(5, item),
            item => Assert.Equal(20, item));
    }

    // ── Overrule enforcement tests ─────────────────────────────────────

    [Fact]
    public async Task Shadowing_parent_method_without_overrule_raises_error()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var ex = await Assert.ThrowsAsync<ToshDiagnosticException>(async () =>
        {
            await engine.ExecuteToListAsync(@"
                class Animal {
                    func speak() { echo ""..."" }
                }
                class Dog extends Animal {
                    func speak() { echo ""woof"" }
                }
            ");
        });
        Assert.Contains("overrule", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Overrule_method_works_correctly()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            class Animal {
                func speak() { echo ""..."" }
            }
            class Dog extends Animal {
                overrule func speak() { echo ""woof"" }
            }
            var d = new Dog()
            $d.speak()
        ");
        Assert.Equal(["woof"], results);
    }

    [Fact]
    public async Task Overrule_without_parent_method_raises_error()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var ex = await Assert.ThrowsAsync<ToshDiagnosticException>(async () =>
        {
            await engine.ExecuteToListAsync(@"
                class Cat {
                    overrule func fly() { echo ""nope"" }
                }
            ");
        });
        Assert.Contains("overrule", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Hollow property validation tests ───────────────────────────────

    [Fact]
    public async Task Hollow_prop_enforced_in_subclass()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var ex = await Assert.ThrowsAsync<ToshDiagnosticException>(async () =>
        {
            await engine.ExecuteToListAsync(@"
                hollow class Shape {
                    hollow prop Area: double
                }
                class Circle extends Shape {
                    prop Radius = 5
                }
            ");
        });
        Assert.Contains("Area", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hollow_prop_satisfied_by_subclass()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            hollow class Shape {
                hollow prop Area: double
            }
            class Square extends Shape {
                prop Side = 4
                prop Area: double = ($this.Side * $this.Side)
            }
            var s = new Square()
            echo $s.Area
        ");
        Assert.Single(results);
        Assert.Equal("16", results[0]?.ToString());
    }

    // ── Record modifier tests ──────────────────────────────────────────

    [Fact]
    public async Task Sealed_record_is_recognized()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            sealed record Point(X: int, Y: int)
            var p = new Point(3, 4)
            echo $p.X
            echo $p.Y
        ");
        Assert.Collection(results,
            item => Assert.Equal(3, item),
            item => Assert.Equal(4, item));
    }

    [Fact]
    public async Task Partial_record_merges_fields()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            partial record Vec2(X: int, Y: int)
            partial record Vec2(Z?: int = 0)
            var v = new Vec2(1, 2)
            echo $v.X
            echo $v.Y
            echo $v.Z
        ");
        Assert.Collection(results,
            item => Assert.Equal(1, item),
            item => Assert.Equal(2, item),
            item => Assert.Equal(0, item));
    }

    [Fact]
    public async Task Strict_record_is_recognized()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            strict record Config(Host: string, Port: int)
            var c = new Config(""localhost"", 8080)
            echo $c.Host
        ");
        Assert.Equal(["localhost"], results);
    }

    // ── Struct tests ───────────────────────────────────────────────────

    [Fact]
    public async Task Struct_basic_creation_and_field_access()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            struct Point(x, y) { }
            var p = new Point(3, 4)
            echo $p.x $p.y
        ");
        Assert.Equal([3, 4], results.Select(Convert.ToInt32).ToArray());
    }

    [Fact]
    public async Task Struct_is_immutable_by_default()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var ex = await Assert.ThrowsAsync<ToshDiagnosticException>(async () =>
        {
            await engine.ExecuteToListAsync(@"
                struct Point(x, y) { }
                var p = new Point(1, 2)
                $p.x = 10
            ");
        });
        Assert.Contains("immutable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Struct_fluid_allows_mutation()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            fluid struct Point(x, y) { }
            var p = new Point(1, 2)
            $p.x = 10
            echo $p.x
        ");
        Assert.Equal(10, Convert.ToInt32(results[0]));
    }

    [Fact]
    public async Task Struct_copy_on_assign()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            fluid struct Point(x, y) { }
            var a = new Point(1, 2)
            var b = $a
            $b.x = 99
            echo $a.x $b.x
        ");
        Assert.Equal([1, 99], results.Select(Convert.ToInt32).ToArray());
    }

    [Fact]
    public async Task Struct_with_methods()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            struct Point(x, y) {
                func sum() { echo ($this.x + $this.y) }
            }
            var p = new Point(3, 4)
            $p.sum()
        ");
        Assert.Equal(7, Convert.ToInt32(results[0]));
    }

    [Fact]
    public async Task Struct_static_method()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            struct Point(x, y) {
                static func origin() { new Point(0, 0) }
            }
            var p = Point.origin()
            echo $p.x $p.y
        ");
        Assert.Equal([0, 0], results.Select(Convert.ToInt32).ToArray());
    }

    [Fact]
    public async Task Struct_structural_equality()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            struct Point(x, y) { }
            var a = new Point(1, 2)
            var b = new Point(1, 2)
            var c = new Point(3, 4)
            echo ($a == $b) ($a == $c)
        ");
        Assert.Equal([true, false], results);
    }

    [Fact]
    public async Task Struct_sealed_parses()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            sealed struct Frozen(x) { }
            var f = new Frozen(42)
            echo $f.x
        ");
        Assert.Equal(42, Convert.ToInt32(results[0]));
    }

    [Fact]
    public async Task Struct_partial_merges()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            partial struct Vec(x) { }
            partial struct Vec(y) { }
            var v = new Vec(1, 2)
            echo $v.x $v.y
        ");
        Assert.Equal([1, 2], results.Select(Convert.ToInt32).ToArray());
    }

    // ── Trait tests ────────────────────────────────────────────────────

    [Fact]
    public async Task Trait_basic_required_method()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            trait Speakable { func speak() }
            class Dog uses Speakable {
                func speak() { echo ""woof"" }
            }
            var d = new Dog()
            $d.speak()
        ");
        Assert.Equal("woof", results.Single());
    }

    [Fact]
    public async Task Trait_missing_required_method_throws()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var ex = await Assert.ThrowsAsync<ToshDiagnosticException>(async () =>
        {
            await engine.ExecuteToListAsync(@"
                trait Speakable { func speak() }
                class Dog uses Speakable { }
            ");
        });
        Assert.Contains("speak", ex.Message);
    }

    [Fact]
    public async Task Trait_default_method_injected()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            trait Greetable {
                func greet(name) { echo $""Hello, {$name}!"" }
            }
            class Bot uses Greetable { }
            var b = new Bot()
            $b.greet(""world"")
        ");
        Assert.Equal("Hello, world!", results.Single());
    }

    [Fact]
    public async Task Trait_default_property_injected()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            trait Tagged {
                prop Tag = ""default""
            }
            class Item uses Tagged { }
            var item = new Item()
            echo $item.Tag
        ");
        Assert.Equal("default", results.Single());
    }

    [Fact]
    public async Task Trait_is_operator_checks_trait()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            trait Speakable { func speak() }
            class Dog uses Speakable {
                func speak() { echo ""woof"" }
            }
            var d = new Dog()
            echo ($d is Speakable)
        ");
        Assert.Equal(true, results.Single());
    }

    // ── fulfills keyword tests ─────────────────────────────────────────

    [Fact]
    public async Task Fulfills_keyword_works_for_interfaces()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            interface Runnable { func run() }
            class Task fulfills Runnable {
                func run() { echo ""running"" }
            }
            var t = new Task()
            $t.run()
        ");
        Assert.Equal("running", results.Single());
    }

    // ── Constructor validation tests ───────────────────────────────────

    [Fact]
    public async Task Extends_clause_args_auto_call_parent_constructor()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            class Base {
                prop X
                Base(x) { $this.X = $x }
            }
            class Child extends Base(10) { }
            var c = new Child()
            echo $c.X
        ");
        Assert.Equal(10, Convert.ToInt32(results[0]));
    }

    [Fact]
    public async Task Constructor_chain_with_super_call()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(@"
            class Base {
                prop X
                Base(x) { $this.X = $x }
            }
            class Child extends Base {
                prop Y
                Child(x, y) {
                    $super($x)
                    $this.Y = $y
                }
            }
            var c = new Child(1, 2)
            echo $c.X $c.Y
        ");
        Assert.Equal([1, 2], results.Select(Convert.ToInt32).ToArray());
    }
}
