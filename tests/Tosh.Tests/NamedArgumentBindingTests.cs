using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// TS-P1-06 — named arguments must be validated rather than silently
/// dropped or overwritten. A duplicate name is invalid for every
/// candidate and is reported at the call site; a name that matches no
/// parameter is reported too, but only after overload selection has had
/// its chance, so a sibling overload declaring that parameter still wins.
/// </summary>
public sealed class NamedArgumentBindingTests
{
    [Fact]
    public async Task Unknown_named_argument_on_a_function_is_diagnosed()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(() =>
            engine.ExecuteToListAsync(
                """
                func f(a, b = 2) { return $"{$a},{$b}" }
                f(1, zzz = 9)
                """));

        Assert.Equal("tosh.runtime.unknown_named_argument", exception.Diagnostics[0].Code);
        Assert.Contains("zzz", exception.Diagnostics[0].Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Duplicate_named_argument_on_a_function_is_diagnosed()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(() =>
            engine.ExecuteToListAsync(
                """
                func f(a, b = 2) { return $"{$a},{$b}" }
                f(1, b = 5, b = 9)
                """));

        Assert.Equal("tosh.runtime.duplicate_named_argument", exception.Diagnostics[0].Code);
    }

    [Fact]
    public async Task Unknown_named_argument_on_a_method_is_diagnosed()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(() =>
            engine.ExecuteToListAsync(
                """
                class C { func m(a, b = 2) { return $"{$a},{$b}" } }
                (new C()).m(1, zzz = 9)
                """));

        Assert.Equal("tosh.runtime.unknown_named_argument", exception.Diagnostics[0].Code);
    }

    [Fact]
    public async Task Duplicate_named_argument_on_a_method_is_diagnosed()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(() =>
            engine.ExecuteToListAsync(
                """
                class C { func m(a, b = 2) { return $"{$a},{$b}" } }
                (new C()).m(1, b = 5, b = 9)
                """));

        Assert.Equal("tosh.runtime.duplicate_named_argument", exception.Diagnostics[0].Code);
    }

    [Fact]
    public async Task A_named_argument_still_selects_the_overload_that_declares_it()
    {
        // The unknown-name rule must not break overload resolution: for
        // the one-parameter candidate 'b' is unknown, so it loses, and
        // the two-parameter candidate wins instead of the whole call
        // failing.
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync(
            """
            func g(a) { return "one" }
            func g(a, b) { return "two" }
            var got = (g(1, b = 2))
            """);

        Assert.True(engine.TryGetVariableValue("got", out var got));
        Assert.Equal("two", got);
    }

    [Fact]
    public async Task Method_overloads_resolve_by_named_argument_too()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync(
            """
            class K {
                func m(a) { return "one" }
                func m(a, b) { return "two" }
            }
            var got = ((new K()).m(1, b = 2))
            """);

        Assert.True(engine.TryGetVariableValue("got", out var got));
        Assert.Equal("two", got);
    }

    [Fact]
    public async Task Valid_named_arguments_bind_out_of_order()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync(
            """
            func deploy(host, port = 22, user = "root") { return $"{$user}@{$host}:{$port}" }
            var got = (deploy(user = "alice", port = 2222, host = "server"))
            """);

        Assert.True(engine.TryGetVariableValue("got", out var got));
        Assert.Equal("alice@server:2222", got);
    }

    [Fact]
    public async Task Rest_receives_only_unconsumed_positional_values_in_order()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await engine.ExecuteToListAsync(
            """
            func f(a, b = 2, rest...) {
                var n = ($rest | count)
                return $"{$a}|{$b}|{$n}"
            }
            var spread = (f(1, 7, 8, 9))
            var defaulted = (f(1))
            """);

        Assert.True(engine.TryGetVariableValue("spread", out var spread));
        Assert.Equal("1|7|2", spread);
        Assert.True(engine.TryGetVariableValue("defaulted", out var defaulted));
        Assert.Equal("1|2|0", defaulted);
    }

    [Fact]
    public async Task An_arity_failure_is_still_reported_as_an_arity_failure()
    {
        // The new name checks must not swallow ordinary arity errors.
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(() =>
            engine.ExecuteToListAsync(
                """
                class C { func m(a, b) { return 1 } }
                (new C()).m(1)
                """));

        Assert.DoesNotContain(
            "unknown_named_argument",
            exception.Diagnostics[0].Code,
            StringComparison.Ordinal);
    }
}
