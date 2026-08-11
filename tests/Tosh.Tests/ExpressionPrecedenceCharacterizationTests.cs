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

    /// <summary>The values a range expression yields, for cases where it is not a scalar.</summary>
    private static async Task<IReadOnlyList<object?>> RangeValuesAsync(string expression)
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);
        return await engine.ExecuteToListAsync($"var __r = ({expression})\n$__r");
    }

    [Theory]
    // `TS-P2-76`, now fixed: `..` sits below arithmetic, so both bounds take a full
    // expression. These were the two cases that failed with "Operator operands
    // 'System.Int32' and 'Tosh.Runtime.ToshRange' are not compatible".
    //
    // The first version of these asserted the *old* failure via ThrowsAnyAsync and kept
    // passing after the fix — because `$__r` on a range replays as three values, so the
    // helper's own `Assert.Single` threw and the test caught that instead of a language
    // error. A test that cannot tell the defect from its own scaffolding is worse than no
    // test, which is why these now assert values.
    [InlineData("1 + 2 .. 5", new object[] { 3L, 4L, 5L })]
    [InlineData("1 .. 2 + 3", new object[] { 1L, 2L, 3L, 4L, 5L })]
    [InlineData("(1 + 2) .. 5", new object[] { 3L, 4L, 5L })]
    public async Task Range_binds_looser_than_arithmetic(string expression, object[] expected)
    {
        var values = await RangeValuesAsync(expression);

        Assert.Equal(expected.Select(Convert.ToInt64), values.Select(Convert.ToInt64));
    }

    [Fact]
    public async Task A_stepped_range_takes_expressions_too()
    {
        // `start .. step .. end`, with the step computed: 1, 4, 7, 10.
        var values = await RangeValuesAsync("1 .. 2 + 1 .. 10");

        Assert.Equal([1L, 4L, 7L, 10L], values.Select(Convert.ToInt64));
    }

    [Fact]
    public async Task Range_still_binds_tighter_than_comparison()
    {
        // Below arithmetic but above comparison, so `1 .. 3 == $x` compares the range
        // rather than ranging over a boolean.
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);
        var results = await engine.ExecuteToListAsync("var r = ((1 .. 3) == (1 .. 3))\n$r");

        Assert.True(Convert.ToBoolean(Assert.Single(results)));
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
