using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

public sealed class AssignmentSemanticsTests
{
    [Fact]
    public async Task Tuple_assignment_evaluates_rhs_once()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            var calls = 0
            func make_values() {
                $calls += 1
                return [10, 20]
            }
            var a = 0
            var b = 0
            ($a, $b) = make_values
            echo $a
            echo $b
            echo $calls
            """);

        Assert.Equal([10L, 20L, 1L], results.Select(Convert.ToInt64).ToArray());
    }

    [Fact]
    public async Task Tuple_assignment_updates_nearest_existing_scope()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            var outer = 1
            if (true) {
                var inner = 2
                ($outer, $inner) = [3, 4]
                echo $inner
            }
            echo $outer
            """);

        Assert.Equal([4L, 3L], results.Select(Convert.ToInt64).ToArray());
    }

    [Fact]
    public async Task Tuple_assignment_rejects_undeclared_target_without_partial_commit()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync("var first = 1");

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("($first, $missing) = [2, 3]"));

        Assert.Contains(exception.Diagnostics, d => d.Code == "tosh.runtime.unknown_variable");
        Assert.Equal(1L, Convert.ToInt64(Assert.Single(
            await engine.ExecuteToListAsync("echo $first"))));
    }

    [Fact]
    public async Task Tuple_assignment_rejects_const_target_without_partial_commit()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync(
            """
            var first = 1
            const second = 2
            """);

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("($first, $second) = [3, 4]"));

        Assert.Contains(exception.Diagnostics, d => d.Code == "tosh.runtime.const_reassignment");
        var results = await engine.ExecuteToListAsync("echo $first\necho $second");
        Assert.Equal([1L, 2L], results.Select(Convert.ToInt64).ToArray());
    }

    [Fact]
    public async Task Tuple_assignment_enforces_annotations_without_partial_commit()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync(
            """
            var first = 1
            var second: int = 2
            """);

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("($first, $second) = [3, \"not-an-int\"]"));

        Assert.Contains(exception.Diagnostics, d => d.Code == "tosh.runtime.annotation_conversion_failed");
        var results = await engine.ExecuteToListAsync("echo $first\necho $second");
        Assert.Equal([1L, 2L], results.Select(Convert.ToInt64).ToArray());
    }

    [Fact]
    public async Task Null_coalescing_assignment_sets_null_variable_and_evaluates_rhs_once()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            var calls = 0
            func make_value() {
                $calls += 1
                return 9
            }
            var value = null
            $value ??= make_value
            echo $value
            echo $calls
            """);

        Assert.Equal([9L, 1L], results.Select(Convert.ToInt64).ToArray());
    }

    [Fact]
    public async Task Null_coalescing_assignment_skips_rhs_for_non_null_variable()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            var calls = 0
            func make_value() {
                $calls += 1
                return 9
            }
            var value = 7
            $value ??= make_value
            echo $value
            echo $calls
            """);

        Assert.Equal([7L, 0L], results.Select(Convert.ToInt64).ToArray());
    }

    [Fact]
    public async Task Null_coalescing_assignment_initializes_allocated_typed_variable()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            var value: int
            $value ??= "5"
            echo $value
            """);

        Assert.Equal(5L, Convert.ToInt64(Assert.Single(results)));
    }

    [Fact]
    public async Task Null_coalescing_assignment_rejects_unknown_variable_without_rhs_effect()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync(
            """
            var calls = 0
            func make_value() {
                $calls += 1
                return 9
            }
            """);

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("$missing ??= make_value"));

        Assert.Contains(exception.Diagnostics, d => d.Code == "tosh.runtime.unknown_variable");
        Assert.Equal(0L, Convert.ToInt64(Assert.Single(
            await engine.ExecuteToListAsync("echo $calls"))));
    }

    [Fact]
    public async Task Null_coalescing_assignment_rejects_const_without_rhs_effect()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync(
            """
            var calls = 0
            func make_value() {
                $calls += 1
                return 9
            }
            const value = null
            """);

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("$value ??= make_value"));

        Assert.Contains(exception.Diagnostics, d => d.Code == "tosh.runtime.const_reassignment");
        Assert.Equal(0L, Convert.ToInt64(Assert.Single(
            await engine.ExecuteToListAsync("echo $calls"))));
    }

    [Fact]
    public async Task Null_coalescing_member_assignment_is_lazy()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            class Box { prop Value = null }
            var calls = 0
            func make_value() {
                $calls += 1
                return 9
            }
            var box = new Box()
            $box.Value ??= make_value
            $box.Value ??= make_value
            echo $box.Value
            echo $calls
            """);

        Assert.Equal([9L, 1L], results.Select(Convert.ToInt64).ToArray());
    }

    [Fact]
    public async Task Null_coalescing_index_assignment_is_lazy()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            var values = { "key" => null }
            var calls = 0
            func make_value() {
                $calls += 1
                return 9
            }
            $values["key"] ??= make_value
            $values["key"] ??= make_value
            echo $values["key"]
            echo $calls
            """);

        Assert.Equal([9L, 1L], results.Select(Convert.ToInt64).ToArray());
    }

    [Fact]
    public async Task Compound_assignment_uses_left_biased_class_operator_dispatch()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            class Offset(value: int) {
                prop Value: int = value
                func -(other) { return ($this.Value - $other.Value) }
            }

            var compound = new Offset(10)
            $compound -= new Offset(3)
            var expanded = (new Offset(10) - new Offset(3))
            echo $compound
            echo $expanded
            """);

        Assert.Equal([7L, 7L], results.Select(Convert.ToInt64).ToArray());
    }

    [Fact]
    public async Task Compound_assignment_preserves_symmetric_right_operand_fallback()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            class ReverseOffset(value: int) {
                prop Value: int = value
                func -(other) { return ($this.Value - $other) }
            }

            var compound = 10
            $compound -= new ReverseOffset(3)
            var expanded = (10 - new ReverseOffset(3))
            echo $compound
            echo $expanded
            """);

        Assert.Equal([-7L, -7L], results.Select(Convert.ToInt64).ToArray());
    }

    [Fact]
    public async Task Member_and_index_compound_assignments_use_class_operators()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            class Addend(value: int) {
                prop Value: int = value
                func +(other) { return ($this.Value + $other.Value) }
            }
            class Holder { prop Item = null }

            var holder = new Holder()
            $holder.Item = new Addend(2)
            $holder.Item += new Addend(3)

            var values = { "item" => new Addend(4) }
            $values["item"] += new Addend(5)

            echo $holder.Item
            echo $values["item"]
            """);

        Assert.Equal([5L, 9L], results.Select(Convert.ToInt64).ToArray());
    }

    [Fact]
    public async Task Compound_assignment_preserves_annotation_conversion()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            var compound: int = 1
            $compound += "2"
            var expanded: int = 1
            $expanded = ($expanded + "2")
            echo $compound
            echo $expanded
            """);

        Assert.Equal([12, 12], results);
        Assert.All(results, value => Assert.IsType<int>(value));
    }

    [Fact]
    public async Task Power_and_floor_division_compound_assignments_are_supported()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            var power = 3
            $power **= 2
            var quotient = 7
            $quotient //= 2
            echo $power
            echo $quotient
            """);

        Assert.Equal([9L, 3L], results.Select(Convert.ToInt64).ToArray());
    }

    [Fact]
    public async Task Compound_and_expanded_operator_failures_share_diagnostics()
    {
        const string compoundSource =
            """
            var value = null
            $value += 1
            """;
        const string expandedSource =
            """
            var value = null
            $value = ($value + 1)
            """;

        var compoundFailure = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => new ToshEngine(ToshRuntime.CreateDefault())
                .ExecuteToListAsync(compoundSource));
        var expandedFailure = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => new ToshEngine(ToshRuntime.CreateDefault())
                .ExecuteToListAsync(expandedSource));

        var compoundDiagnostic = Assert.Single(compoundFailure.Diagnostics);
        var expandedDiagnostic = Assert.Single(expandedFailure.Diagnostics);
        Assert.Equal(expandedDiagnostic.Code, compoundDiagnostic.Code);
        Assert.Equal(expandedDiagnostic.Title, compoundDiagnostic.Title);
        Assert.Equal(expandedDiagnostic.Label, compoundDiagnostic.Label);
    }

    [Fact]
    public async Task Compound_class_operator_preserves_user_throw_payload()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            class Exploder {
                func +(other) { throw "boom" }
            }
            class Holder { prop Value = null }

            try {
                var value = new Exploder()
                $value += 1
            } catch (error) {
                echo $error
            }

            var holder = new Holder()
            $holder.Value = new Exploder()
            try {
                $holder.Value += 1
            } catch (error) {
                echo $error
            }

            var values = { "item" => new Exploder() }
            try {
                $values["item"] += 1
            } catch (error) {
                echo $error
            }
            """);

        Assert.Equal(["boom", "boom", "boom"], results);
    }
}
