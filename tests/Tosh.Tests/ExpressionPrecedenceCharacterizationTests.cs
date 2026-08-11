using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// What the expression grammar actually binds, pinned by execution — <c>TS-P2-11</c>.
/// </summary>
/// <remarks>
/// <para>
/// The item asks for "an explicit precedence/postfix architecture, preferably Pratt-style,
/// without changing accepted syntax unintentionally". Measured, the expression layers are
/// <em>already</em> an explicit precedence cascade — nine levels from
/// <c>ParseTernaryExpression</c> down to <c>ParseUnaryExpression</c>, conventional and
/// readable. Flattening them into a Pratt table would be a stylistic change across a
/// 13,000-line parser with regression risk and nothing user-visible; the scatter the row
/// describes is at <em>statement</em> dispatch, where ~30 <c>LooksLike*</c> predicates
/// live, and Pratt does not touch that.
/// </para>
/// <para>
/// So this is the half of the item worth having: the corpus its own status line says was
/// started. It states the binding rules as executable facts, which is what makes any
/// later restructuring safe — and writing it immediately found two defects that a
/// refactor would have preserved without noticing, filed as <c>TS-P2-76</c> (range binds
/// tighter than arithmetic) and <c>TS-P2-77</c> (a parenthesised left operand ends a
/// <c>for</c> source early).
/// </para>
/// <para>
/// Everything here asserts <em>current</em> behaviour. Where current behaviour is wrong it
/// is marked and points at its item, so a reader can tell a rule from a defect.
/// </para>
/// </remarks>
public sealed class ExpressionPrecedenceCharacterizationTests
{
    private static async Task<object?> EvalAsync(string expression)
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);
        var results = await engine.ExecuteToListAsync($"var __r = ({expression})\n$__r");
        return Assert.Single(results);
    }

    // ── Arithmetic ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("2 + 3 * 4", 14L)]              // * over +
    [InlineData("(2 + 3) * 4", 20L)]
    [InlineData("2 * 3 + 4", 10L)]
    [InlineData("10 - 3 - 2", 5L)]              // - is left-associative
    [InlineData("100 / 10 / 2", 5L)]            // / is left-associative
    [InlineData("2 + 3 % 2", 3L)]               // % with *
    [InlineData("-2 + 3", 1L)]                  // unary binds tighter
    public async Task Arithmetic_binds_as_written(string expression, long expected)
    {
        Assert.Equal(expected, Convert.ToInt64(await EvalAsync(expression)));
    }

    [Fact]
    public async Task Exponentiation_is_right_associative()
    {
        // 2 ** (3 ** 2) = 512, not (2 ** 3) ** 2 = 64. The one associativity that differs
        // from the rest of the arithmetic chain.
        Assert.Equal(512L, Convert.ToInt64(await EvalAsync("2 ** 3 ** 2")));
    }

    [Fact]
    public async Task Exponentiation_binds_tighter_than_multiplication()
    {
        Assert.Equal(18L, Convert.ToInt64(await EvalAsync("2 * 3 ** 2")));
    }

    // ── Comparison and logic ───────────────────────────────────────────────────

    [Theory]
    [InlineData("2 + 3 == 5", true)]            // arithmetic over ==
    [InlineData("1 < 2 == true", true)]
    [InlineData("true or false and false", true)]   // `and` over `or`
    [InlineData("(true or false) and false", false)]
    [InlineData("not true and false", false)]   // unary `not` over `and`
    [InlineData("not (true and false)", true)]
    public async Task Comparison_and_logic_bind_as_written(string expression, bool expected)
    {
        Assert.Equal(expected, Convert.ToBoolean(await EvalAsync(expression)));
    }

    [Fact]
    public async Task Null_coalescing_sits_below_comparison()
    {
        Assert.Equal(5L, Convert.ToInt64(await EvalAsync("null ?? 5")));
        Assert.Equal(true, Convert.ToBoolean(await EvalAsync("(null ?? 5) == 5")));
    }

    [Fact]
    public async Task The_ternary_is_the_outermost_level()
    {
        // Both arms and the condition take full expressions without parentheses.
        Assert.Equal(7L, Convert.ToInt64(await EvalAsync("1 + 1 == 2 ? 3 + 4 : 9")));
    }

    // ── Postfix ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Member_access_binds_tighter_than_arithmetic()
    {
        Assert.Equal(4L, Convert.ToInt64(await EvalAsync("\"abc\".Length + 1")));
    }

    [Fact]
    public async Task Indexing_binds_tighter_than_arithmetic()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);
        var results = await engine.ExecuteToListAsync(
            """
            var xs = [10, 20, 30]
            var r = ($xs[0] + 1)
            $r
            """);

        Assert.Equal(11L, Convert.ToInt64(Assert.Single(results)));
    }

    // ── Ranges: current behaviour, and it is wrong ─────────────────────────────

    [Fact]
    public async Task A_parenthesised_range_produces_its_values()
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);
        var results = await engine.ExecuteToListAsync("var r = ((1 + 2) .. 5)\n($r | count)");

        Assert.Equal(3, Convert.ToInt32(Assert.Single(results)));
    }

    [Theory]
    // `TS-P2-76`: `..` binds *tighter* than arithmetic, so these are `1 + (2 .. 5)` and
    // `(1 .. 2) + 3` and fail on operand types. Pinned as the current behaviour, not
    // endorsed — every comparable language gives `..` the lower precedence, and the
    // diagnostic a reader meets talks about `Int32` and `ToshRange` rather than grouping.
    [InlineData("1 + 2 .. 5")]
    [InlineData("1 .. 2 + 3")]
    public async Task Range_currently_outranks_arithmetic_which_is_TS_P2_76(string expression)
    {
        await Assert.ThrowsAnyAsync<Exception>(() => EvalAsync(expression));
    }

    // ── Parenthesisation is always available ───────────────────────────────────

    [Theory]
    [InlineData("((1 + 2) * (3 + 4))", 21L)]
    [InlineData("(((1)))", 1L)]
    [InlineData("(2 ** (1 + 1))", 4L)]
    public async Task Explicit_grouping_always_wins(string expression, long expected)
    {
        Assert.Equal(expected, Convert.ToInt64(await EvalAsync(expression)));
    }
}
