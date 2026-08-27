using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Overflow, as §Overflow states it — `TOAST-0018`.
/// </summary>
/// <remarks>
/// <para>
/// The concern was filed as "no `checked` policy in `OperatorEvaluator`", and the survey
/// expected the answer to be a choice between wrapping, saturating and raising. Measuring
/// found a fourth policy already in place and better than all three: integer arithmetic
/// **promotes** to arbitrary precision, so `int.MaxValue + 1` is `2147483648` rather than
/// `-2147483648`. Nothing had to be decided about `+`, `-` or `*` — only written down.
/// </para>
/// <para>
/// `**` was the exception: it computed through `Math.Pow` and dropped to `double` as soon
/// as the result left `int` range, so `2 ** 62` lost its low bits although the exact value
/// fits a `long` and `2 * 2 * …` would have promoted. It now promotes with its neighbours.
/// </para>
/// </remarks>
public sealed class OverflowSemanticsTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? string.Empty : results[^1]?.ToString() ?? "null";
    }

    private static async Task<Exception> ThrowsAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        return await Assert.ThrowsAnyAsync<Exception>(() => engine.ExecuteToListAsync(source));
    }

    private const string MaxInt = "var m = 2147483647 as int\n";

    /// <summary>Integer arithmetic promotes rather than wrapping.</summary>
    [Theory]
    [InlineData(MaxInt + "($m + 1)", "2147483648")]
    [InlineData(MaxInt + "($m * 2)", "4294967294")]
    [InlineData("(9223372036854775807 + 1)", "9223372036854775808")]
    [InlineData("var n = -2147483648 as int\n(0 - $n)", "2147483648")]
    public async Task Integer_arithmetic_promotes(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// The wrapped answer is what this is not.
    /// </summary>
    /// <remarks>
    /// Stated as its own assertion because "promotes" and "does not wrap" fail
    /// differently: a wrapping implementation would answer `-2147483648`, which is a
    /// plausible-looking number, and an equality against the exact value would not say
    /// what went wrong.
    /// </remarks>
    [Fact]
    public async Task Overflow_does_not_wrap_into_a_negative()
    {
        var result = await RunAsync(MaxInt + "($m + 1)");

        Assert.NotEqual("-2147483648", result);
        Assert.Equal("2147483648", result);
    }

    /// <summary>
    /// `**` is exact for a non-negative integer exponent, and narrows when it fits.
    /// </summary>
    [Theory]
    [InlineData("(2 ** 10)", "1024", "Int32")]
    [InlineData("(2 ** 62)", "4611686018427387904", "BigInteger")]
    [InlineData("(2 ** 100)", "1267650600228229401496703205376", "BigInteger")]
    [InlineData("(2 ** 0)", "1", "Int32")]
    public async Task Exponentiation_is_exact_for_integer_powers(
        string source,
        string expected,
        string typeName)
    {
        Assert.Equal(expected, await RunAsync(source));
        Assert.Equal(typeName, await RunAsync($"{source}.GetType().Name"));
    }

    /// <summary>
    /// A fractional or negative exponent is a different operation and stays floating.
    /// </summary>
    [Theory]
    [InlineData("(2 ** 0.5)", "Double")]
    [InlineData("(2 ** -1)", "Double")]
    [InlineData("(2.5 ** 2)", "Double")]
    public async Task A_non_integer_power_is_floating_point(string source, string typeName)
        => Assert.Equal(typeName, await RunAsync($"{source}.GetType().Name"));

    /// <summary>
    /// Past a million, the exponent stops being computed exactly.
    /// </summary>
    /// <remarks>
    /// A memory bound, not a preference: `BigInteger.Pow` allocates in proportion to the
    /// exponent, so an innocuous-looking line could ask for a gigabyte-scale integer. The
    /// test sits just past the bound so it never builds the large value.
    /// </remarks>
    [Fact]
    public async Task A_very_large_exponent_falls_back_to_floating_point()
        => Assert.Equal("Double", await RunAsync("(2 ** 1000001).GetType().Name"));

    /// <summary>
    /// Promotion is a property of the arithmetic, not a licence to store anything.
    /// </summary>
    [Fact]
    public async Task A_declared_type_still_bounds_what_is_stored()
    {
        // The expression is exact...
        Assert.Equal("2147483648", await RunAsync("var x: int = 2147483647\n($x + 1)"));

        // ...and storing it back into an `int` is refused.
        await ThrowsAsync("var x: int = 2147483647\n$x = $x + 1\n$x");
    }

    /// <summary>An out-of-range conversion raises rather than truncating.</summary>
    [Theory]
    [InlineData("(300 as byte)")]
    [InlineData("(2147483648 as int)")]
    public async Task An_out_of_range_conversion_raises(string source)
        => await ThrowsAsync(source);

    /// <summary>Floating point follows IEEE; integer division by zero does not.</summary>
    /// <remarks>
    /// Observed through interpolation, which is the rendering the language specifies
    /// (§Value Rendering). A raw `ToString` gives .NET's `∞`, and pinning that would be
    /// pinning the host's formatting rather than Tōast's.
    /// </remarks>
    [Theory]
    [InlineData("1.0 / 0.0", "Infinity")]
    [InlineData("-1.0 / 0.0", "-Infinity")]
    [InlineData("1e308 * 10", "Infinity")]
    [InlineData("0.0 / 0.0", "NaN")]
    public async Task Floating_point_overflow_gives_an_infinity(string source, string expected)
        => Assert.Equal(expected, await RunAsync($"echo $\"{{{source}}}\""));

    [Theory]
    [InlineData("(1 / 0)")]
    [InlineData("(1 % 0)")]
    public async Task Integer_division_by_zero_raises(string source)
        => Assert.Contains("zero", (await ThrowsAsync(source)).Message, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A promoted value is the same value as the integer it equals — including as a key.
    /// </summary>
    /// <remarks>
    /// Found while surveying overflow, and it was a hole this session opened: key equality
    /// reduces a number to what decides its identity, and `BigInteger` was not in that
    /// switch. So a promoted `2147483647` was `==` to the ordinary one and a *different
    /// key*, which would have put one value in two dictionary entries. The rule is that a
    /// `BigInteger` inside `long` range normalises to `long`; nothing else can equal one
    /// outside it.
    /// </remarks>
    [Fact]
    public async Task A_promoted_integer_keys_with_the_integer_it_equals()
    {
        const string RoundTrip = "var big = (2147483647 as int) + 1 - 1\n";

        Assert.Equal("True", await RunAsync(RoundTrip + "($big == 2147483647)"));
        Assert.Equal("BigInteger", await RunAsync(RoundTrip + "$big.GetType().Name"));

        // Two values, one key: the sentinel keeps `count` measuring items.
        Assert.Equal("2", await RunAsync(RoundTrip + "[$big, 2147483647, \"z\"] | distinct | count"));

        // And a value too large for any `long` keys only with itself.
        Assert.Equal(
            "3",
            await RunAsync("var huge = 9223372036854775807 + 1\n[$huge, 1, \"z\"] | distinct | count"));
    }

    /// <summary>The key relation agrees with equality about promoted values, directly.</summary>
    [Fact]
    public void The_key_comparer_normalises_a_big_integer()
    {
        var promoted = new System.Numerics.BigInteger(2147483647);

        Assert.True(ShellKeyComparer.Instance.Equals(promoted, 2147483647));
        Assert.Equal(
            ShellKeyComparer.Instance.GetHashCode(promoted),
            ShellKeyComparer.Instance.GetHashCode(2147483647));
    }
}
