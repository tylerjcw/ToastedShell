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

    // ── TOAST-0115: unparenthesised, in assignment position ───────────────────
    //
    // Every case above is wrapped in `var r = (…)`, and the parentheses are what made
    // them pass: `_expressionDepth` is raised by `(`, `[` and collection literals and by
    // nothing else, so TS-P2-02's break could not fire on a bare right-hand side.
    // `var u = -$x` reported `Command '-$x' was not found` while `- $x` and `(-$x)` both
    // worked.

    [Theory]
    [InlineData("var r = -$x", -3L)]
    [InlineData("var r = +$x", 3L)]
    [InlineData("var r = 0\n$r = -$x", -3L)]
    [InlineData("var r = 10\n$r -= -$x", 13L)]
    [InlineData("var r = 1\n$r += -$x", -2L)]
    public async Task A_glued_sign_negates_without_parentheses(string assignment, long expected)
    {
        Assert.Equal(expected, Convert.ToInt64(await EvalAsync($"var x = 3\n{assignment}\n$r")));
    }

    // ── TOAST-0115, widened: every position where a flag is impossible ────────
    //
    // Assignment was not the only unbracketed value position. `2 + -$x` was worse than
    // the reported case rather than better: it did not fail, it concatenated the literal
    // text and answered the string "2-$x". The test is now on how the *stage* opened
    // rather than on the token immediately before, so `2 * -$x` negates while
    // `echo a -$x` keeps its arguments, without a curated list of operators.

    [Theory]
    [InlineData("-$x", -3d)]                        // command-name position: a name never starts with a sign
    [InlineData("+$x", 3d)]
    [InlineData("2 + -$x", -1d)]                    // the stage opened with a number, so it is not a command
    [InlineData("2 - -$x", 5d)]
    [InlineData("2 * -$x", -6d)]
    [InlineData("2 ** -$x", 0.125d)]
    [InlineData("$x + -$x", 0d)]                    // …or with a `$` word
    public async Task A_glued_sign_negates_wherever_a_flag_is_impossible(string statement, double expected)
    {
        Assert.Equal(expected, Convert.ToDouble(await EvalAsync($"var x = 3\n{statement}")), 6);
    }

    [Fact]
    public async Task A_glued_sign_negates_after_a_string_operand()
    {
        Assert.Equal("a-3", $"{await EvalAsync("var x = 3\n\"a\" + -$x")}");
    }

    [Theory]
    [InlineData("func f() { return -$x }\nf", -3L)]      // `return` takes a value, never a flag
    [InlineData("func f() => -$x\nf", -3L)]              // an arrow body is a value position
    [InlineData("if (true) { -$x }", -3L)]               // a block opens a statement
    [InlineData("var r = 0; -$x", -3L)]                  // and so does `;`
    public async Task A_glued_sign_negates_after_a_statement_boundary(string script, long expected)
    {
        Assert.Equal(expected, Convert.ToInt64(await EvalAsync($"var x = 3\n{script}")));
    }

    [Fact]
    public async Task A_class_can_negate_its_own_member_without_parentheses()
    {
        var script = "class C(v) {\n"
            + "    prop X = $v\n"
            + "    func Neg() { return -$this.X }\n"
            + "}\n"
            + "(new C(4)).Neg()";

        Assert.Equal(-4L, Convert.ToInt64(await EvalAsync(script)));
    }

    [Fact]
    public async Task A_command_combinator_still_wants_a_command_on_its_right()
    {
        // `|`, `&&` and `||` each want a command, so the sign is left glued: breaking it
        // would only trade `Command '-$x' was not found` for `Command '-' was not found`.
        var error = await Assert.ThrowsAnyAsync<Exception>(() => EvalAsync("var x = 3\n1 | -$x"));

        Assert.Contains("-$x", error.Message);
    }

    [Theory]
    [InlineData("echo -$x", "-$x")]
    [InlineData("echo --n=5", "--n=5")]
    [InlineData("echo --name", "--name")]
    [InlineData("echo a$x", "a$x")]
    [InlineData("echo ../p", "../p")]
    public async Task Command_argument_position_still_reads_a_flag(string command, string expected)
    {
        // The break must stay off in argument position: `-$x` there is a flag, and that is
        // the reason TS-P2-02 was narrow in the first place.
        Assert.Equal(expected, $"{await EvalAsync($"var x = 3\n{command}")}");
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
