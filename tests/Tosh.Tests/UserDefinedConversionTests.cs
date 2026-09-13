using Tosh.Runtime;
using Tosh.Tui.Widgets;

namespace Tosh.Tests;

/// <summary>
/// Conversions a type declares about itself, honoured when binding a value to a property.
/// </summary>
/// <remarks>
/// This is what lets a layout be written <c>Size = "2*"</c> instead of
/// <c>Size = TuiLength.Star(2)</c>. <see cref="TuiLength"/> is used as the subject because
/// it is the reason the support exists, and because a test that pins a real caller is
/// worth more than one that pins a fixture.
/// </remarks>
public sealed class UserDefinedConversionTests
{
    [Theory]
    [InlineData("*", 1)]
    [InlineData("2*", 2)]
    [InlineData(" 3* ", 3)]
    public void A_star_string_converts_to_a_star_length(string text, int weight)
    {
        Assert.True(TypeConversion.TryConvert(text, typeof(TuiLength), out var converted));
        Assert.Equal(TuiLength.Star(weight), converted);
    }

    [Fact]
    public void The_other_spellings_convert_too()
    {
        Assert.True(TypeConversion.TryConvert("auto", typeof(TuiLength), out var auto));
        Assert.Equal(TuiLength.Auto, auto);

        Assert.True(TypeConversion.TryConvert("12", typeof(TuiLength), out var cells));
        Assert.Equal(TuiLength.Fixed(12), cells);

        Assert.True(TypeConversion.TryConvert(12, typeof(TuiLength), out var number));
        Assert.Equal(TuiLength.Fixed(12), number);
    }

    [Fact]
    public void A_type_declaring_no_such_conversion_is_as_unconvertible_as_before()
    {
        Assert.False(TypeConversion.TryConvert("nonsense", typeof(NoConversions), out _));
    }

    [Fact]
    public void A_target_that_cannot_be_boxed_is_not_a_target()
    {
        // `string` declares an implicit conversion to `ReadOnlySpan<char>`, and reflection
        // cannot hand one back. Overload resolution asks about types like this, so saying
        // "yes" here made `Int32.Parse("42")` bind to the span overload and then throw.
        Assert.False(TypeConversion.TryConvert("42", typeof(ReadOnlySpan<char>), out _));
    }

    private sealed class NoConversions;
}
