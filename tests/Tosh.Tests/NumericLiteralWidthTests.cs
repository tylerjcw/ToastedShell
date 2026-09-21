using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// An integer literal takes the narrowest type that holds it, in every base.
///
/// `TS-P2-123`. Hex was parsed into a `long`, so `0xFFFFFFFFFFFFFFFF` became -1 by
/// two's complement and then *fitted `int`* — a 64-bit mask silently truncated to
/// `Int32 -1`. Decimal past `long.MaxValue` fell through to `double`, so
/// `18446744073709551615` was `Double 1.8446744073709552E+19` and the upper half
/// of `ulong` could not be written at all: `var v = (18446744073709551614 as ulong)`
/// failed with *Cannot convert 'Double' to 'ulong'*, while the same value passed
/// straight through a native `ulong` parameter worked — the marshalling was never
/// at fault.
///
/// Found while auditing native interop, where full-width masks and sentinel handles
/// are ordinary things to write.
///
/// The rule is now one rule for every base: `int`, then `long`, then `ulong`, and a
/// literal that fits none of them is a diagnostic rather than a different number.
/// Suffixes `u`, `L` and `UL` pin the type where inference is not what is wanted.
/// </summary>
public class NumericLiteralWidthTests
{
    private static async Task<string> TypeOfAsync(string literal)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync($"({literal}).GetType().Name");
        return results.Single()?.ToString() ?? "null";
    }

    private static async Task<string> ValueOfAsync(string literal)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync($"\"\" + {literal}");
        return results.Single()?.ToString() ?? "null";
    }

    /// <summary>
    /// The boundaries, decimal. Each pair is the largest value of a type and the
    /// first value past it, which is where a width rule either holds or does not.
    /// </summary>
    [Theory]
    [InlineData("2147483647", "Int32")]              // int.MaxValue
    [InlineData("2147483648", "Int64")]              // one past
    [InlineData("9223372036854775807", "Int64")]     // long.MaxValue
    [InlineData("9223372036854775808", "UInt64")]    // one past — was Double
    [InlineData("18446744073709551615", "UInt64")]   // ulong.MaxValue — was Double
    public async Task A_decimal_literal_takes_the_narrowest_type(string literal, string expected)
        => Assert.Equal(expected, await TypeOfAsync(literal));

    /// <summary>
    /// The same rule in hex, which is where the truncation lived. `0xFFFFFFFFFFFFFFFF`
    /// is the case that was silently wrong: `Int32 -1`.
    /// </summary>
    [Theory]
    [InlineData("0x7FFFFFFF", "Int32")]
    [InlineData("0xFFFFFFFF", "Int64")]
    [InlineData("0x7FFFFFFFFFFFFFFF", "Int64")]
    [InlineData("0x8000000000000000", "UInt64")]     // was Int64, negative
    [InlineData("0xFFFFFFFFFFFFFFFE", "UInt64")]     // was Int32 -2
    [InlineData("0xFFFFFFFFFFFFFFFF", "UInt64")]     // was Int32 -1
    public async Task A_hex_literal_follows_the_same_rule(string literal, string expected)
        => Assert.Equal(expected, await TypeOfAsync(literal));

    /// <summary>Binary and octal are the same rule again, not a third one.</summary>
    [Theory]
    [InlineData("0b1010", "Int32")]
    [InlineData("0b1111111111111111111111111111111111", "Int64")]
    [InlineData("0o777", "Int32")]
    [InlineData("0o777777777777777", "Int64")]
    public async Task Binary_and_octal_follow_it_too(string literal, string expected)
        => Assert.Equal(expected, await TypeOfAsync(literal));

    /// <summary>
    /// The value has to survive the widening, not just the type. A mask read as the
    /// wrong width is the whole defect, so the number is asserted as well.
    /// </summary>
    [Theory]
    [InlineData("0xFFFFFFFFFFFFFFFF", "18446744073709551615")]
    [InlineData("18446744073709551615", "18446744073709551615")]
    [InlineData("0xDEADBEEFCAFE", "244837814094590")]
    [InlineData("1_000_000", "1000000")]
    public async Task The_value_is_preserved_across_the_widening(string literal, string expected)
        => Assert.Equal(expected, await ValueOfAsync(literal));

    /// <summary>Suffixes pin the type where inference is not what is wanted.</summary>
    [Theory]
    [InlineData("100u", "UInt32")]
    [InlineData("100U", "UInt32")]
    [InlineData("5000000000u", "UInt64")]   // past uint, so the suffix widens
    [InlineData("100L", "Int64")]
    [InlineData("100l", "Int64")]
    [InlineData("100UL", "UInt64")]
    [InlineData("100ul", "UInt64")]
    [InlineData("100lu", "UInt64")]
    [InlineData("0xFFu", "UInt32")]
    public async Task A_suffix_pins_the_type(string literal, string expected)
        => Assert.Equal(expected, await TypeOfAsync(literal));

    /// <summary>
    /// Signed literals are unchanged, including `long.MinValue`, whose magnitude is
    /// one past `long.MaxValue` — so the sign has to be applied before the range
    /// check, not after.
    /// </summary>
    [Theory]
    [InlineData("-5", "Int32")]
    [InlineData("-2147483648", "Int32")]
    [InlineData("-9223372036854775808", "Int64")]
    public async Task Signed_literals_are_unchanged(string literal, string expected)
        => Assert.Equal(expected, await TypeOfAsync(literal));

    /// <summary>
    /// A decimal literal past every fixed integer type is a <c>BigInteger</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// **This reverses part of an earlier decision, deliberately.** The rule was "past every
    /// integer type is a diagnostic", and the reasoning recorded here was that the behaviour
    /// being removed — *becoming a `double`* — "is silent, and it loses digits". Both halves
    /// of that objection were about `double`. A `BigInteger` loses nothing: it is exact at any
    /// width, and `bigint` is already a blessed alias.
    /// </para>
    /// <para>
    /// What forced it is that `Int128`, `UInt128` and `bigint` are all nameable and none of
    /// them could be written down. `var y: Int128 = 170141183460469231731687303715884105727`
    /// failed in the lexer before the annotation was read, and the help line advised computing
    /// the value "at runtime where a wider numeric type applies" — the language declining to
    /// express a value it has a type for. `TOAST-0138`.
    /// </para>
    /// <para>
    /// The silence objection survives in weaker form: a typo'd extra digit now yields a
    /// `BigInteger` rather than a diagnostic. That is the trade, and it is recorded rather
    /// than glossed.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_decimal_literal_past_every_integer_type_is_a_big_integer()
        => Assert.Equal("BigInteger", await TypeOfAsync("99999999999999999999999"));

    /// <summary>
    /// A literal that states its own width still overflows: it said which width it meant.
    /// </summary>
    [Theory]
    [InlineData("0xFFFFFFFFFFFFFFFFF")]
    [InlineData("99999999999999999999L")]
    public async Task A_literal_past_every_integer_type_is_refused(string literal)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var exception = await Assert.ThrowsAnyAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync($"writeline {literal}"));

        Assert.Equal(
            "tosh.parser.numeric_literal_overflow",
            Assert.Single(exception.Diagnostics).Code);
    }

    /// <summary>
    /// Real numbers are untouched — the integer path must decline them rather than
    /// claim them, or `3.5` and `1e3` would become diagnostics.
    /// </summary>
    [Theory]
    [InlineData("3.5", "Double")]
    [InlineData("1e3", "Double")]
    [InlineData("1.5e-3", "Double")]
    public async Task Floating_point_literals_are_untouched(string literal, string expected)
        => Assert.Equal(expected, await TypeOfAsync(literal));

    /// <summary>
    /// The case that sent me here: a full-width mask reaching a native `ulong`
    /// parameter. This failed with *Cannot convert 'Double' to 'ulong'* before.
    /// </summary>
    [Fact]
    public async Task A_full_width_mask_converts_to_ulong()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync("(0xFFFFFFFFFFFFFFFF as ulong).ToString()");

        Assert.Equal("18446744073709551615", results.Single()?.ToString());
    }
}
