using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// TS-P1-05 — one callable default-binding protocol for free functions,
/// lambdas, class methods, and constructors: omitted defaults evaluate at
/// call time, in the callable's lexical environment, left-to-right, with
/// earlier bound parameters visible. Losing overload candidates and
/// explicitly provided arguments never evaluate a default.
/// </summary>
public sealed class CallableDefaultBindingTests
{
    [Fact]
    public async Task Free_function_default_sees_earlier_bound_parameter()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync(
            """
            var got = null
            func f(a, b = $a + 1) { return $b }
            $got = (f 5)
            """);

        Assert.True(engine.TryGetVariableValue("got", out var got));
        Assert.Equal(6, got);
    }

    [Fact]
    public async Task Free_function_defaults_chain_left_to_right()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync(
            """
            var got = null
            func f(a, b = $a + 1, c = $b + 1) { return $"{$a},{$b},{$c}" }
            $got = (f 1)
            """);

        Assert.True(engine.TryGetVariableValue("got", out var got));
        Assert.Equal("1,2,3", got);
    }

    [Fact]
    public async Task Free_function_default_evaluates_at_call_time_in_lexical_scope()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync(
            """
            var g = 1
            func f(x = $g) { return $x }
            var first = (f)
            $g = 2
            var second = (f)
            """);

        Assert.True(engine.TryGetVariableValue("first", out var first));
        Assert.Equal(1, first);
        Assert.True(engine.TryGetVariableValue("second", out var second));
        Assert.Equal(2, second);
    }

    [Fact]
    public async Task Named_argument_binds_out_of_order_and_defaults_fill_the_gap()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync(
            """
            var got = null
            func f(a, b = $a + 1, c = $b * 10) { return $"{$a},{$b},{$c}" }
            $got = (f(1, c = 99))
            """);

        Assert.True(engine.TryGetVariableValue("got", out var got));
        Assert.Equal("1,2,99", got);
    }

    [Fact]
    public async Task Lambda_default_sees_earlier_bound_parameter()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync(
            """
            var f = func(a, b = $a * 3) => ($b)
            var got = ($f(2))
            """);

        Assert.True(engine.TryGetVariableValue("got", out var got));
        Assert.Equal(6, got);
    }

    [Fact]
    public async Task Class_method_default_evaluates_with_earlier_parameter_visible()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync(
            """
            class C { func m(a, b = $a * 2) { return $b } }
            var got = ((new C()).m(5))
            """);

        Assert.True(engine.TryGetVariableValue("got", out var got));
        Assert.Equal(10, got);
    }

    [Fact]
    public async Task Static_method_default_evaluates_with_earlier_parameter_visible()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync(
            """
            class S { shared func m(a, b = $a + 100) { return $b } }
            var got = (S.m(1))
            """);

        Assert.True(engine.TryGetVariableValue("got", out var got));
        Assert.Equal(101, got);
    }

    [Fact]
    public async Task Primary_constructor_default_evaluates_with_earlier_parameter_visible()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync(
            """
            class P(x, y = $x * 10) { prop Y = $y }
            var got = ((new P(3)).Y)
            """);

        Assert.True(engine.TryGetVariableValue("got", out var got));
        Assert.Equal(30, got);
    }

    [Fact]
    public async Task Explicit_constructor_default_is_bound_before_the_body_runs()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync(
            """
            class C { prop V = 0; C(a, b = 7) { $this.V = $b } }
            var got = ((new C(1)).V)
            """);

        Assert.True(engine.TryGetVariableValue("got", out var got));
        Assert.Equal(7, got);
    }

    [Fact]
    public async Task Defaults_reevaluate_on_every_call()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync(
            """
            var n = 0
            func probe() { $n += 1; return $n }
            func f(x = (probe)) { return $x }
            var first = (f())
            var second = (f())
            """);

        Assert.True(engine.TryGetVariableValue("first", out var first));
        Assert.Equal(1, first);
        Assert.True(engine.TryGetVariableValue("second", out var second));
        Assert.Equal(2, second);
        Assert.True(engine.TryGetVariableValue("n", out var count));
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Provided_arguments_suppress_default_evaluation()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync(
            """
            var hits = 0
            func side() { $hits += 1; return 9 }
            func g(a, b = (side)) { return $b }
            var got = (g 1 5)
            """);

        Assert.True(engine.TryGetVariableValue("got", out var got));
        Assert.Equal(5, got);
        Assert.True(engine.TryGetVariableValue("hits", out var hits));
        Assert.Equal(0, hits);
    }

    [Fact]
    public async Task Losing_overload_candidates_never_evaluate_their_defaults()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync(
            """
            var hits = 0
            func side() { $hits += 1; return 9 }
            class K {
                func m(a) { return "one" }
                func m(a, b = (side)) { return "two" }
            }
            var got = ((new K()).m(1))
            """);

        Assert.True(engine.TryGetVariableValue("got", out var got));
        Assert.Equal("one", got);
        Assert.True(engine.TryGetVariableValue("hits", out var hits));
        Assert.Equal(0, hits);
    }

    [Fact]
    public async Task Typed_default_converts_through_the_parameter_annotation()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync(
            """
            func f(x: int = "7") { return ($x + 1) }
            var got = (f)
            """);

        Assert.True(engine.TryGetVariableValue("got", out var got));
        Assert.Equal(8, got);
    }

    [Fact]
    public async Task Unconvertible_default_produces_the_structured_diagnostic()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(() =>
            engine.ExecuteToListAsync(
                """
                func f(x: int = "nope") { return $x }
                f
                """));

        Assert.Equal("tosh.runtime.parameter_default_conversion_failed", exception.Diagnostics[0].Code);
    }

    [Fact]
    public async Task Default_referencing_a_later_parameter_is_an_unknown_variable()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(() =>
            engine.ExecuteToListAsync(
                """
                func f(a = $b, b = 2) { return $a }
                f
                """));

        Assert.Equal("tosh.runtime.unknown_variable", exception.Diagnostics[0].Code);
    }

    [Fact]
    public async Task Rest_parameters_still_collect_unconsumed_positional_arguments()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync(
            """
            func f(a, b = $a + 1, rest...) { return $"{$a},{$b},{$rest | count}" }
            var defaulted = (f 1)
            var spread = (f 1 2 3 4)
            """);

        Assert.True(engine.TryGetVariableValue("defaulted", out var defaulted));
        Assert.Equal("1,2,0", defaulted);
        Assert.True(engine.TryGetVariableValue("spread", out var spread));
        Assert.Equal("1,2,2", spread);
    }
}
