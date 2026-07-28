using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// TS-P1-14 — cross-type comparison is strict and symmetric. Ordering
/// never silently picks a comparison domain (so `"10" &lt; 9` is a
/// failure rather than a lexicographic `true`), the same operand pair
/// behaves identically whichever way it is written, and equality has no
/// case-insensitive ToString fallback.
/// </summary>
public sealed class ComparisonSemanticsTests
{
    [Theory]
    [InlineData("abc", 5)]
    [InlineData("10", 9)]
    public void A_string_never_orders_against_a_number(string text, int number)
    {
        Assert.Throws<InvalidOperationException>(() =>
            OperatorEvaluator.EvaluateBinary(text, "<", number));

        Assert.Throws<InvalidOperationException>(() =>
            OperatorEvaluator.EvaluateBinary(number, ">", text));
    }

    [Fact]
    public void Ordering_is_symmetric_for_the_same_operand_pair()
    {
        // Before the fix `"abc" < 5` answered false while `5 > "abc"`
        // threw, so the two spellings of one comparison disagreed.
        var forward = Record.Exception(() => OperatorEvaluator.EvaluateBinary("abc", "<", 5));
        var reverse = Record.Exception(() => OperatorEvaluator.EvaluateBinary(5, ">", "abc"));

        Assert.NotNull(forward);
        Assert.NotNull(reverse);
        Assert.Equal(forward!.GetType(), reverse!.GetType());
    }

    [Fact]
    public void Booleans_have_no_ordering()
    {
        Assert.Throws<InvalidOperationException>(() =>
            OperatorEvaluator.EvaluateBinary(true, "<", 3));

        Assert.Throws<InvalidOperationException>(() =>
            OperatorEvaluator.EvaluateBinary(3, ">", true));
    }

    [Theory]
    [InlineData("a", "b", true)]
    [InlineData("b", "a", false)]
    public void Strings_still_order_against_strings(string left, string right, bool expected)
    {
        Assert.Equal(expected, OperatorEvaluator.EvaluateBinary(left, "<", right));
    }

    [Theory]
    [InlineData(1, 2.5, true)]
    [InlineData(2.5, 1, false)]
    public void Numeric_widening_still_orders(object left, object right, bool expected)
    {
        Assert.Equal(expected, OperatorEvaluator.EvaluateBinary(left, "<", right));
    }

    [Fact]
    public void Numeric_ordering_agrees_in_both_directions()
    {
        Assert.Equal(true, OperatorEvaluator.EvaluateBinary(1, "<", 2.5));
        Assert.Equal(true, OperatorEvaluator.EvaluateBinary(2.5, ">", 1));
    }

    [Fact]
    public void Equality_coerces_a_fully_parsing_numeric_string()
    {
        Assert.Equal(true, OperatorEvaluator.EvaluateBinary(1, "==", "1"));
        Assert.Equal(false, OperatorEvaluator.EvaluateBinary(1, "==", "1abc"));
    }

    [Fact]
    public void Equality_has_no_case_insensitive_tostring_fallback()
    {
        // Mixed-type equality used to fold case through ToString() while
        // string-to-string equality stayed case-sensitive, so the two
        // disagreed about what "equal" meant. Case sensitivity is now
        // uniform. A value still equals its own text form, because
        // TypeConversion genuinely converts it — but only in the casing
        // it actually produces.
        var marker = new TextShapedValue();

        Assert.Equal(false, OperatorEvaluator.EvaluateBinary("ABC", "==", "abc"));
        Assert.Equal(false, OperatorEvaluator.EvaluateBinary(marker, "==", "marker"));
        Assert.Equal(false, OperatorEvaluator.EvaluateBinary("marker", "==", marker));

        // Conversion-backed equality survives, and agrees both ways.
        Assert.Equal(true, OperatorEvaluator.EvaluateBinary(marker, "==", "MARKER"));
        Assert.Equal(true, OperatorEvaluator.EvaluateBinary("MARKER", "==", marker));
    }

    [Fact]
    public void Conversion_backed_equality_is_unaffected()
    {
        // Removing the ToString fallback must not disturb equality that
        // rests on a genuine conversion: numeric strings still parse,
        // and a CLR enum still compares against its member name (which
        // Enum parsing accepts case-insensitively).
        Assert.Equal(true, OperatorEvaluator.EvaluateBinary(1, "==", "1"));
        Assert.Equal(true, OperatorEvaluator.EvaluateBinary(StringComparison.Ordinal, "==", "Ordinal"));
    }

    /// <summary>
    /// Has no conversion to or from <see cref="string"/>, but its text
    /// form would have matched under the old ToString-based fallback.
    /// </summary>
    private sealed class TextShapedValue
    {
        public override string ToString() => "MARKER";
    }

    [Fact]
    public void Collection_equality_is_still_element_wise()
    {
        Assert.Equal(
            true,
            OperatorEvaluator.EvaluateBinary(new[] { 1, 2 }, "==", new[] { 1, 2 }));
        Assert.Equal(
            false,
            OperatorEvaluator.EvaluateBinary(new[] { 1, 2 }, "==", new[] { 1, 3 }));
    }

    [Fact]
    public void Null_ordering_still_yields_false_in_nullable_contexts()
    {
        Assert.False(OperatorEvaluator.EvaluateOrderedComparison(null, 1, nullable: true, c => c < 0));
        Assert.False(OperatorEvaluator.EvaluateOrderedComparison(1, null, nullable: true, c => c < 0));
    }
}
