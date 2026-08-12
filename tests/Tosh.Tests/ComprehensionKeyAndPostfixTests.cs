using Tosh.Language;
using Tosh.Language.Parsing;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A comprehension key and a postfix condition are expressions — <c>TS-P2-17</c>,
/// <c>TS-P2-19</c>.
/// </summary>
/// <remarks>
/// <para>
/// Both are the shape this programme keeps finding: a position parsing a *primary* where
/// an expression belongs, joining <c>TS-P2-72</c> (index slots and collection values),
/// <c>TS-P2-76</c> (range operands) and <c>TS-P2-77</c> (a <c>for</c> source).
/// </para>
/// <para>
/// <c>{% $x % 2 => $x &lt;| for x in 1..4 %}</c> reported <c>expected_fat_arrow</c>,
/// because the key stopped after <c>$x</c> and the parser met <c>%</c> where it wanted
/// <c>=&gt;</c> — while the *value* had always taken a full expression.
/// <c>return "big" if $x &gt; 5</c> reported "Block statements must be separated by a
/// newline or ';'", a message about separators for a defect in the condition.
/// </para>
/// <para>
/// In both cases the diagnostic named the delimiter the parser was looking for rather
/// than the expression it had stopped reading, which is why neither reads like a
/// precedence problem from the outside.
/// </para>
/// </remarks>
public sealed class ComprehensionKeyAndPostfixTests
{
    private static async Task<object?> EvalAsync(string script)
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);
        return Assert.Single(await engine.ExecuteToListAsync(script));
    }

    // ── TS-P2-17: dict-comprehension keys ──────────────────────────────────────

    [Fact]
    public async Task A_dict_comprehension_key_takes_an_operator_expression()
    {
        // `$x % 2` over 1..4 gives two distinct keys, 0 and 1.
        var value = await EvalAsync(
            """
            var d = ({% $x % 2 => $x <| for x in 1..4 %})
            ($d.Keys | count)
            """);

        Assert.Equal(2, Convert.ToInt32(value));
    }

    [Fact]
    public async Task Both_key_and_value_may_be_expressions()
    {
        var value = await EvalAsync(
            """
            var d = ({% $x * 10 => $x + 1 <| for x in 1..3 %})
            ($d.Keys | count)
            """);

        Assert.Equal(3, Convert.ToInt32(value));
    }

    [Fact]
    public async Task A_plain_key_still_works()
    {
        var value = await EvalAsync(
            """
            var d = ({% $x => $x <| for x in 1..4 %})
            ($d.Keys | count)
            """);

        Assert.Equal(4, Convert.ToInt32(value));
    }

    [Theory]
    // The other comprehension forms share the generalised lookahead and must be
    // unaffected by parameterising it over its terminator.
    [InlineData("var s = ({: $x * 2 <| for x in 1..3 :})\n($s | count)", 3)]
    [InlineData("var l = [$x * 2 <| for x in 1..3]\n($l | count)", 3)]
    public async Task The_other_comprehensions_are_unchanged(string script, int expected)
    {
        Assert.Equal(expected, Convert.ToInt32(await EvalAsync(script)));
    }

    // ── TS-P2-19: postfix conditions ───────────────────────────────────────────

    [Theory]
    [InlineData("return \"big\" if $x > 5", 9, "big")]
    [InlineData("return \"big\" if ($x > 5)", 9, "big")]
    [InlineData("return \"big\" if $x > 5 and $x < 100", 9, "big")]
    [InlineData("return \"small\" unless $x > 5", 1, "small")]
    public async Task A_postfix_condition_takes_a_full_expression(string body, int input, string expected)
    {
        var value = await EvalAsync($"func f(x: int) {{ {body} }}\n(f {input})");

        Assert.Equal(expected, value?.ToString());
    }

    [Fact]
    public async Task A_false_postfix_condition_still_skips_the_jump()
    {
        // Skipping the `return` means the function yields *nothing*, not null — so this
        // asserts an empty result rather than a single null one.
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);

        var results = await engine.ExecuteToListAsync("func f(x: int) { return \"big\" if $x > 5 }\n(f 1)");

        Assert.Empty(results);
    }

    [Fact]
    public void An_omitted_condition_reports_the_documented_diagnostic()
    {
        // The specification names `tosh.parser.expected_postfix_condition` for this, and
        // it was being pre-empted by a generic `unexpected_token` from parsing straight
        // into the closing brace.
        var result = ToshParser.Parse("func f(x: int) { return \"big\" if }", "<probe>");

        Assert.Contains(result.Diagnostics, d => d.Code == "tosh.parser.expected_postfix_condition");
    }

    [Fact]
    public void A_bare_variable_condition_still_parses()
    {
        var result = ToshParser.Parse("func f(x: int) { return \"big\" if $x }", "<probe>");

        Assert.Empty(result.Diagnostics);
    }
}
