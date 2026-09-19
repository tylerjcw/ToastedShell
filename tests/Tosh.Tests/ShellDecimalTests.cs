using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// What a floating value means when it is asked for as a <see cref="decimal"/>.
/// </summary>
/// <remarks>
/// <para>
/// The language defines this rather than inheriting it. Through .NET 10 the platform's
/// <c>(decimal)aDouble</c> rounded to 15 significant digits; from .NET 11 it writes the
/// double's full binary value, so the same script produced <c>483.06</c> on one runtime and
/// <c>483.0600000000000023</c> on the next, and <c>0.1 as decimal == 0.1</c> went from true
/// to false without a line of this repository changing.
/// </para>
/// <para>
/// These tests are the reason the framework can stay one string to change: they fail on both
/// runtimes if the rule is ever left to the platform again.
/// </para>
/// </remarks>
public sealed class ShellDecimalTests
{
    /// <summary>
    /// The decimal a double denotes is the number it is spelled with.
    /// </summary>
    /// <remarks>
    /// An author who writes <c>0.1</c> means a tenth. The double standing in for it is not
    /// exactly a tenth, but the shortest spelling that reads back as that double is
    /// <c>0.1</c> — so that is what it denotes.
    /// </remarks>
    [Theory]
    [InlineData(0.1, "0.1")]
    [InlineData(483.06, "483.06")]
    [InlineData(0.3, "0.3")]
    [InlineData(1.0, "1")]
    [InlineData(-2.5, "-2.5")]
    [InlineData(1e10, "10000000000")]
    public void A_double_denotes_the_decimal_it_is_spelled_with(double value, string expected)
    {
        Assert.True(ShellDecimal.TryFromDouble(value, out var result));
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), result);
    }

    /// <summary>
    /// Two doubles that differ get two decimals that differ.
    /// </summary>
    /// <remarks>
    /// This is the property the 15-digit round broke, and the whole reason the rule is
    /// round-tripping rather than rounding. Collapsing <c>0.3</c> and
    /// <c>0.30000000000000004</c> onto one value made a comparison mean something different
    /// depending on whether it happened to be constant-folded — see
    /// <c>ConstantFolder.NumericEq</c>.
    /// </remarks>
    [Fact]
    public void Doubles_that_differ_do_not_collapse_onto_one_decimal()
    {
        Assert.True(ShellDecimal.TryFromDouble(0.3, out var third));
        Assert.True(ShellDecimal.TryFromDouble(0.30000000000000004, out var nudged));

        Assert.NotEqual(third, nudged);
    }

    /// <summary>
    /// A float round-trips as a float, not as the double it widens to.
    /// </summary>
    /// <remarks>
    /// <c>0.1f</c> widens to <c>0.10000000149011612</c>. Those digits are an artefact of the
    /// widening — the value was only ever precise to a float, and claiming otherwise invents
    /// precision that was never measured.
    /// </remarks>
    [Fact]
    public void A_float_is_read_at_a_float_s_precision()
    {
        Assert.True(ShellDecimal.TryFromSingle(0.1f, out var result));
        Assert.Equal(0.1m, result);
    }

    /// <summary>A value with no decimal at all is declined, not approximated.</summary>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(1e300)]
    [InlineData(-1e300)]
    public void A_value_outside_decimal_is_declined(double value)
        => Assert.False(ShellDecimal.TryFromDouble(value, out _));

    /// <summary>The integral types need no rule of ours; they convert exactly.</summary>
    [Fact]
    public void Integers_convert_exactly()
    {
        Assert.True(ShellDecimal.TryFrom(42, out var fromInt));
        Assert.Equal(42m, fromInt);

        Assert.True(ShellDecimal.TryFrom(long.MaxValue, out var fromLong));
        Assert.Equal(9223372036854775807m, fromLong);

        Assert.True(ShellDecimal.TryFrom(7.5m, out var fromDecimal));
        Assert.Equal(7.5m, fromDecimal);
    }

    /// <summary>Anything that is not a number has no decimal.</summary>
    [Fact]
    public void A_non_number_has_no_decimal()
    {
        Assert.False(ShellDecimal.TryFrom("3", out _));
        Assert.False(ShellDecimal.TryFrom(null, out _));
        Assert.False(ShellDecimal.TryFrom(new object(), out _));
    }

    /// <summary>
    /// The throwing form is for callers that have already established the value is finite.
    /// </summary>
    [Fact]
    public void The_throwing_form_refuses_rather_than_returning_another_number()
    {
        Assert.Equal(483.06m, ShellDecimal.FromDouble(483.06));
        Assert.Throws<OverflowException>(() => ShellDecimal.FromDouble(double.NaN));
    }
}
