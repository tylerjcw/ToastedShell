using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// `x as T` is a cast, and it binds tighter than the arithmetic around it.
///
/// `TS-P2-105`: `as` used to sit in the *comparison* operator set, whose right
/// operand is parsed as a full additive expression — so `x as int % 2` read
/// `int % 2` as the type and reported `Operator '%' is not compatible with
/// operand types 'String' and 'Int32'`. Nothing in that diagnostic pointed at the
/// cast, and the parentheses it wanted are not suggested by the shape of the
/// expression.
///
/// `is` stays where it was on purpose, and the last test here is the control for
/// that: it yields a boolean, so `1 + 2 is int` should test the sum.
/// </summary>
public class CastPrecedenceTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return string.Join(",", results.Select(value => value?.ToString() ?? "null"));
    }

    /// <summary>The reported shape, reduced.</summary>
    [Theory]
    [InlineData("7 as int % 2", "1")]
    [InlineData("7 as int * 2", "14")]
    [InlineData("7 as int + 2", "9")]
    [InlineData("7 as int - 2", "5")]
    [InlineData("7 as int / 2", "3")]
    [InlineData("7 as int // 2", "3")]
    public async Task A_cast_binds_tighter_than_the_arithmetic_after_it(string expression, string expected)
        => Assert.Equal(expected, await RunAsync(expression));

    /// <summary>
    /// Exponentiation is the case that decides where the cast level goes. It sits
    /// *below* `**` rather than above multiplicative, because a cast folded in
    /// above would leave `** 3` with nothing to attach to.
    /// </summary>
    [Fact]
    public async Task A_cast_binds_tighter_than_exponentiation()
        => Assert.Equal("8", await RunAsync("2 as int ** 3"));

    [Theory]
    [InlineData("7 as int == 3", "False")]
    [InlineData("7 as int > 3", "True")]
    [InlineData("7 as int != 3", "True")]
    public async Task A_cast_binds_tighter_than_comparison(string expression, string expected)
        => Assert.Equal(expected, await RunAsync(expression));

    /// <summary>The cast applies to the left operand only, not the whole sum.</summary>
    [Fact]
    public async Task A_cast_on_the_right_of_an_operator_takes_only_its_own_operand()
        => Assert.Equal("3", await RunAsync("1 + 2 as int"));

    [Fact]
    public async Task A_string_cast_composes_with_concatenation()
        => Assert.Equal("42!", await RunAsync("var x = 42\n$x as string + \"!\""));

    [Fact]
    public async Task Casts_chain_left_to_right()
        => Assert.Equal("7", await RunAsync("7 as int as string"));

    /// <summary>
    /// The control for the regression this fix caused on the way in. Six separate
    /// "does this look like an expression?" scans enumerate the operator
    /// predicates by hand, so removing `as` from the comparison set silently
    /// dropped it from all of them — and a cast with no *other* operator beside it
    /// stopped parsing as an expression at all, reporting "insert '|' before the
    /// next command". Every case above has a second operator and so none of them
    /// would have caught it.
    /// </summary>
    [Theory]
    [InlineData("var x = 42\n$x as int", "42")]
    [InlineData("var x = 42\n$x as string", "42")]
    [InlineData("7 as decimal", "7")]
    public async Task A_cast_standing_alone_is_still_an_expression(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// `is` is deliberately not moved. It produces a boolean, so binding it
    /// tightly would make `1 + 2 is int` mean `1 + (2 is int)`.
    /// </summary>
    [Fact]
    public async Task A_type_test_still_binds_looser_than_arithmetic()
        => Assert.Equal("True", await RunAsync("1 + 2 is int"));
}
