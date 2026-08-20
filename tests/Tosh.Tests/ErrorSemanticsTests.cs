using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Errors and `catch`, as §Errors and catch states it — `TOAST-0018`.
/// </summary>
/// <remarks>
/// <para>
/// The box asked what is catchable, what a thrown non-error value means, and how a
/// `no_clr` target represents one. The first two are answered and specified here. The
/// third is `TOAST-0029`: a raised diagnostic answers only to the implementation type name
/// it happens to have, which a target without the CLR cannot reproduce.
/// </para>
/// <para>
/// Two defects were fixed on the way, both found by one probe. A class declared
/// `extends Error` was **not** `is Error` — the CLR base was matched by name, and `Error`
/// is the alias for `ToshError` — so a user-defined error landed in the same bucket as a
/// thrown string. And the CLR base was consulted only on the instance's *own* definition,
/// so two levels of inheritance from a built-in matched nothing at all: `E2 extends E1
/// extends Error` was not `is Error`, `is ToshError` or `is Exception`.
/// </para>
/// </remarks>
public sealed class ErrorSemanticsTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? string.Empty : results[^1]?.ToString() ?? "null";
    }

    /// <summary>Anything can be thrown, and arrives unchanged.</summary>
    [Theory]
    [InlineData("throw \"a plain string\"", "a plain string")]
    [InlineData("throw 42", "42")]
    [InlineData("throw new Error(\"boom\")", "boom")]
    public async Task Any_value_can_be_thrown(string thrown, string expected)
        => Assert.Equal(
            expected,
            await RunAsync($"try {{ {thrown} }} catch (e) {{ echo $\"{{if ($e is Error) {{ $e.Message }} else {{ $e }}}}\" }}"));

    /// <summary>
    /// A declared error type is an `Error`, at any depth.
    /// </summary>
    /// <remarks>
    /// `is Error` was false for both of these until 2026-08-20 — the first because the
    /// alias was never resolved, the second because an intermediate class's CLR base was
    /// never consulted.
    /// </remarks>
    [Theory]
    [InlineData("class E1 extends Error { }\n((new E1()) is Error)", "True")]
    [InlineData("class E1 extends Error { }\nclass E2 extends E1 { }\n((new E2()) is Error)", "True")]
    [InlineData("((new Error(\"x\")) is Error)", "True")]
    public async Task A_declared_error_type_is_an_Error(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>The CLR spellings work at depth too, and an unrelated class is unaffected.</summary>
    [Theory]
    [InlineData("class E1 extends Error { }\nclass E2 extends E1 { }\n((new E2()) is ToshError)", "True")]
    [InlineData("class E1 extends Error { }\nclass E2 extends E1 { }\n((new E2()) is Exception)", "True")]
    [InlineData("class P { }\nclass Q extends P { }\n((new Q()) is Error)", "False")]
    [InlineData("class P { }\nclass Q extends P { }\n((new Q()) is P)", "True")]
    public async Task The_base_walk_is_unchanged_where_it_already_worked(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// The property the fix exists for: a handler can tell an error from a thrown value.
    /// </summary>
    [Fact]
    public async Task A_handler_can_distinguish_an_error_from_a_plain_value()
    {
        const string Classify = """
            class AppError extends Error { }
            func classify(thrown) {
                try { throw $thrown } catch (e) {
                    if ($e is Error) { echo "error" } else { echo "value" }
                }
            }

            """;

        Assert.Equal("value", await RunAsync(Classify + "classify \"oops\""));
        Assert.Equal("value", await RunAsync(Classify + "classify 42"));
        Assert.Equal("error", await RunAsync(Classify + "classify (new Error(\"x\"))"));
        Assert.Equal("error", await RunAsync(Classify + "classify (new AppError())"));
    }

    /// <summary>
    /// A raised runtime error is a diagnostic, and is deliberately not an `Error`.
    /// </summary>
    /// <remarks>
    /// One is the language reporting that an operation had no answer; the other is a
    /// program raising something on purpose. The distinction is intended. What is *not*
    /// settled is the spelling — a diagnostic answers only to its implementation type
    /// name, which `TOAST-0029` carries.
    /// </remarks>
    [Theory]
    [InlineData("(1 / 0)", "Division by zero.")]
    [InlineData("var x = null\n$x.Length", "Cannot read member 'Length' of null. Use '?.' to yield null instead.")]
    public async Task A_runtime_error_is_a_catchable_diagnostic(string body, string message)
    {
        Assert.Equal(
            "False",
            await RunAsync($"try {{ {body} }} catch (e) {{ ($e is Error) }}"));

        Assert.Equal(
            message,
            await RunAsync($"try {{ {body} }} catch (e) {{ echo $\"{{$e.Message}}\" }}"));
    }

    /// <summary>`finally` runs, and an inner one runs before an outer `catch`.</summary>
    [Fact]
    public async Task Finally_runs_and_ordering_is_inside_out()
    {
        Assert.Equal("after", await RunAsync("try { echo \"body\" } finally { echo \"after\" }"));

        // The inner `finally` completes before the outer handler sees the error.
        Assert.Equal(
            "outer:inner",
            await RunAsync("""
                var order = ""
                try {
                    try { throw "inner" } finally { $order = "inner" }
                } catch (e) { $order = $"outer:{$order}" }
                echo $order
                """));
    }

    /// <summary>Re-raising carries the value onward.</summary>
    [Fact]
    public async Task A_caught_value_can_be_re_raised()
        => Assert.Equal(
            "outer got one",
            await RunAsync("""
                try {
                    try { throw "one" } catch (e) { throw $e }
                } catch (e) { echo $"outer got {$e}" }
                """));
}
