using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A range's two operands parse at the same precedence — `TOAST-0024`.
/// </summary>
/// <remarks>
/// <para>
/// They did not. `ParseRangeExpression` reads the left operand with
/// `ParseBitwiseOrExpression` and then handed the right one to `ParseRangeArgument`, which
/// parsed it with `ParseAdditiveExpression` — tighter by four levels. So `1 bor 2 .. 4`
/// parsed and `1 .. 2 bor 4` did not, reporting an unclosed expression at the `bor`, and
/// the same line in statement position reported a missing pipeline separator instead.
/// Neither message named the cause.
/// </para>
/// <para>
/// The precedence table places `..` immediately looser than `bor`, so both operands belong
/// at the bitwise-or level. Found writing `TOAST-0003`'s precedence guard, where the
/// natural expression for distinguishing `bor` from `..` turned out not to parse.
/// </para>
/// </remarks>
public sealed class RangeOperandPrecedenceTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? string.Empty : results[^1]?.ToString() ?? "null";
    }

    /// <summary>
    /// Every bitwise level works in the right operand, as it always did in the left.
    /// </summary>
    [Theory]
    [InlineData("1 .. 2 bor 4", "6")]      // 2 bor 4 = 6, so 1..6
    [InlineData("1 .. 2 shl 2", "8")]      // 2 shl 2 = 8, so 1..8
    [InlineData("1 .. 6 band 3", "2")]     // 6 band 3 = 2, so 1..2
    [InlineData("1 .. 3 bxor 1", "2")]     // 3 bxor 1 = 2, so 1..2
    public async Task The_right_operand_parses_the_bitwise_levels(string range, string expectedCount)
        => Assert.Equal(expectedCount, await RunAsync($"(({range}) | count)"));

    /// <summary>The left operand is unchanged, pinned as the control it always was.</summary>
    [Theory]
    [InlineData("1 bor 2 .. 4", "2")]      // (1 bor 2)..4 = 3..4
    [InlineData("1 shl 2 .. 5", "2")]      // 4..5
    public async Task The_left_operand_is_unchanged(string range, string expectedCount)
        => Assert.Equal(expectedCount, await RunAsync($"(({range}) | count)"));

    /// <summary>
    /// The forms that reported a parse error now run, in both positions.
    /// </summary>
    /// <remarks>
    /// Statement position failed differently from expression position — "Expression
    /// pipeline stages must be separated by '|'" against "A closing ')' is required here" —
    /// which is why the cause took a while to see. Both are pinned.
    /// </remarks>
    [Fact]
    public async Task Both_positions_parse()
    {
        Assert.Equal("6", await RunAsync("((1 .. 2 bor 4) | count)"));
        Assert.Equal("6", await RunAsync("var r = 1 .. 2 bor 4\n($r | count)"));
    }

    /// <summary>
    /// Ordinary ranges are untouched, including the stepped and argument forms.
    /// </summary>
    /// <remarks>
    /// The argument form matters: in a command's arguments a range operand stays
    /// primary-only, which is what stops `seq 1..5` swallowing what follows it. Only the
    /// *expression* form moved.
    /// </remarks>
    [Theory]
    [InlineData("((1 .. 3) | count)", "3")]
    [InlineData("((1 .. 2 + 3) | count)", "5")]
    [InlineData("((0 .. 2 .. 8) | count)", "5")]
    [InlineData("(1..5 | count)", "5")]
    [InlineData("var xs = 1 .. 3\n($xs | count)", "3")]
    public async Task Ordinary_ranges_are_unchanged(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));
}
