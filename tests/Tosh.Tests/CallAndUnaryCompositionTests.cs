using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Calls and unary operators compose inside expressions — <c>TS-P2-01</c>,
/// <c>TS-P2-02</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>TS-P2-01</c></b> was filed as "lowercase user calls do not compose", but the
/// case is not about casing — <c>Fx() + 1</c> failed identically. A bareword was the one
/// primary that skipped <c>ParsePostfixChain</c>, so <c>(f() + 1)</c> read <c>f</c> as a
/// word and left <c>()</c> for nobody, reporting "this operator expression never closes"
/// against the outer paren. <c>(f())</c> worked only because it has no top-level operator
/// and took the command-subexpression path instead, which is why the symptom looked like
/// it was about operators. Once the parser built the invocation, the runtime had to learn
/// that a bareword target names a function rather than holding one.
/// </para>
/// <para>
/// <b><c>TS-P2-02</c></b> was two defects. <c>EvaluateUnary</c> implemented only
/// <c>!</c>/<c>not</c>, so <c>- $x</c> reported "Unsupported unary operator '-'" although
/// the parser had always accepted it; and the lexer scanned <c>-$x</c> as a single word,
/// reporting <c>Command '-$x' was not found</c>. Unary also sat below exponentiation, so
/// <c>-$x ** 2</c> was <c>(-$x) ** 2</c>.
/// </para>
/// </remarks>
public sealed class CallAndUnaryCompositionTests
{
    private static async Task<object?> EvalAsync(string script)
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);
        return Assert.Single(await engine.ExecuteToListAsync(script));
    }

    // ── TS-P2-01: calls compose ────────────────────────────────────────────────

    [Theory]
    [InlineData("var r = (f() + 1)", 4L)]
    [InlineData("var r = (1 + f())", 4L)]
    [InlineData("var r = (f() * f())", 9L)]
    [InlineData("var r = ((f()) + 1)", 4L)]
    [InlineData("var r = (f() == 3)", null)]
    public async Task A_call_composes_with_operators(string body, object? expected)
    {
        var value = await EvalAsync($"func f() {{ return 3 }}\n{body}\n$r");

        if (expected is null)
        {
            Assert.True(Convert.ToBoolean(value));
        }
        else
        {
            Assert.Equal(expected, Convert.ToInt64(value));
        }
    }

    [Fact]
    public async Task Casing_was_never_the_issue()
    {
        // The row said "lowercase user calls". A capitalised name failed identically, so
        // the framing pointed at the wrong property.
        Assert.Equal(4L, Convert.ToInt64(await EvalAsync("func Fx() { return 3 }\nvar r = (Fx() + 1)\n$r")));
    }

    [Fact]
    public async Task A_call_with_arguments_composes()
    {
        Assert.Equal(11L, Convert.ToInt64(await EvalAsync("func g(a) { return $a * 2 }\nvar r = (g(5) + 1)\n$r")));
    }

    [Fact]
    public async Task A_callable_held_in_a_variable_still_composes()
    {
        // The path that already worked: the target evaluates to a callable rather than
        // naming one, so it must not have been broken by teaching barewords to resolve.
        Assert.Equal(6L, Convert.ToInt64(await EvalAsync("var h = func(x) => ($x + 1)\nvar r = ($h(4) + 1)\n$r")));
    }

    [Fact]
    public async Task A_name_that_is_not_callable_still_says_so()
    {
        var error = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => EvalAsync("var notAFunction = 5\nvar r = ($notAFunction(1) + 1)\n$r"));

        Assert.Contains(error.Diagnostics, d => d.Code == "tosh.runtime.value_not_callable");
    }

    // ── TS-P2-02: unary operators ──────────────────────────────────────────────

    [Theory]
    [InlineData("- $x", -3L)]
    [InlineData("-$x", -3L)]     // glued: the lexer used to scan this as one word
    [InlineData("+ $x", 3L)]
    [InlineData("+$x", 3L)]
    [InlineData("4 + -$x", 1L)]
    [InlineData("-$x + 1", -2L)]
    [InlineData("-$x * 2", -6L)]
    public async Task Unary_applies_to_a_variable(string expression, long expected)
    {
        Assert.Equal(expected, Convert.ToInt64(await EvalAsync($"var x = 3\nvar r = ({expression})\n$r")));
    }

    [Fact]
    public async Task Unary_negation_works_on_a_double()
    {
        Assert.Equal(-3.5d, Convert.ToDouble(await EvalAsync("var x = 3.5\nvar r = (- $x)\n$r")));
    }

    [Theory]
    // Unary binds looser than `**`, so `-$x ** 2` is `-(x ** 2)` — the reading Python and
    // Ruby give. The right operand stays unary, so `$x ** -1` still parses.
    [InlineData("-$x ** 2", -4L)]
    [InlineData("- $x ** 2", -4L)]
    [InlineData("-$x * 2", -4L)]
    public async Task Unary_binds_looser_than_exponentiation(string expression, long expected)
    {
        Assert.Equal(expected, Convert.ToInt64(await EvalAsync($"var x = 2\nvar r = ({expression})\n$r")));
    }

    [Fact]
    public async Task An_exponent_may_still_be_negative()
    {
        Assert.Equal(0.5d, Convert.ToDouble(await EvalAsync("var x = 2\nvar r = ($x ** -1)\n$r")));
    }

    [Fact]
    public async Task A_negative_literal_is_still_one_token()
    {
        // `-2 ** 2` is 4, not -4, because `-2` lexes as a negative *literal* rather than
        // as unary minus applied to 2. That is a lexing choice and is left alone: making
        // it -4 would mean not gluing `-` to any numeric literal, which reaches far
        // beyond this item. Pinned so the difference is deliberate rather than a surprise.
        Assert.Equal(4L, Convert.ToInt64(await EvalAsync("var r = (-2 ** 2)\n$r")));
        Assert.Equal(-4L, Convert.ToInt64(await EvalAsync("var r = (0 - 2 ** 2)\n$r")));
    }

    // ── Nothing that already worked changed ────────────────────────────────────

    [Theory]
    [InlineData("2 ** 3 ** 2", 512L)]   // right-associative
    [InlineData("2 * 3 ** 2", 18L)]     // ** over *
    [InlineData("2 + 3 * 4", 14L)]
    public async Task Existing_precedence_is_unchanged(string expression, long expected)
    {
        Assert.Equal(expected, Convert.ToInt64(await EvalAsync($"var r = ({expression})\n$r")));
    }
}
