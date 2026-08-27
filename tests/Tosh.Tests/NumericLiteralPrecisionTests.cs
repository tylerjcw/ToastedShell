using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A literal too precise for a `double` becomes a `decimal` — `TOAST-0026`.
/// </summary>
/// <remarks>
/// <para>
/// Every non-integer literal was parsed as a `double`, so `1.0000000000000001` arrived as
/// `1.0` and `1.0000000000000001 as decimal` was already `1.0` before the cast could do
/// anything. The one type people reach for when rounding is unacceptable was the one that
/// could not be written down.
/// </para>
/// <para>
/// A suffix would have been the conventional answer and is unavailable: `1.5m` is 1.5
/// *minutes* and `1.5d` is 1.5 *days*. `M` is free only because suffix matching happens to
/// be case-sensitive here, and making decimal-versus-minutes hinge on the case of one
/// letter is the kind of trap this project keeps removing.
/// </para>
/// <para>
/// The cost of the chosen rule is that a literal's **type depends on its digits**. That is
/// stated plainly rather than hidden, and the boundary is pinned below in both directions.
/// </para>
/// </remarks>
public sealed class NumericLiteralPrecisionTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? string.Empty : results[^1]?.ToString() ?? "null";
    }

    /// <summary>An ordinary literal is still a `double`.</summary>
    /// <remarks>
    /// Two cases decide the rule's shape. `0.1` has no exact `double`, so widening every
    /// literal that is inexact in binary would make nearly all of them decimals — the
    /// question is whether the *literal's digits* survived, not whether the value is
    /// representable. And `2.718281828459045` has sixteen significant digits, more than
    /// the fifteen `(decimal)theDouble` keeps: comparing against that conversion widened
    /// it although its `double` holds every digit. The comparison is against the
    /// `double`'s round-trip form instead, which is what "the double kept it" means.
    /// </remarks>
    [Theory]
    [InlineData("1.5")]
    [InlineData("0.1")]
    [InlineData("3.14159")]
    [InlineData("2.718281828459045")]
    // Outside decimal's range entirely: a double is the only thing that can hold it.
    [InlineData("1e300")]
    public async Task An_ordinary_literal_is_a_double(string literal)
        => Assert.Equal("Double", await RunAsync($"(({literal}).GetType().Name)"));

    /// <summary>A literal carrying digits the double would drop becomes a `decimal`.</summary>
    [Theory]
    [InlineData("1.0000000000000001")]
    [InlineData("3.14159265358979323846")]
    public async Task An_over_precise_literal_is_a_decimal(string literal)
        => Assert.Equal("Decimal", await RunAsync($"(({literal}).GetType().Name)"));

    /// <summary>
    /// And the digits survive, which is the whole point.
    /// </summary>
    [Fact]
    public async Task The_precision_is_kept_rather_than_rounded()
    {
        Assert.Equal("1.0000000000000001", await RunAsync("echo $\"{1.0000000000000001}\""));

        // It was `1.0` before, so this comparison was true.
        Assert.Equal("False", await RunAsync("(1.0000000000000001 == 1.0)"));

        // And the cast it was filed for now has something left to cast.
        Assert.Equal(
            "False",
            await RunAsync("var a = 1.0000000000000001 as decimal\nvar b = 1.0 as decimal\n($a == $b)"));
    }

    /// <summary>
    /// Double arithmetic is untouched, including the case everyone knows.
    /// </summary>
    /// <remarks>
    /// If widening had been applied more eagerly, `0.1 + 0.2` would answer `0.3` and the
    /// language would have quietly changed what a floating-point number is. It answers
    /// what IEEE says.
    /// </remarks>
    [Theory]
    [InlineData("(0.1 + 0.2)", "0.30000000000000004")]
    [InlineData("(1.5 + 1.5)", "3")]
    [InlineData("(1.5).GetType().Name", "Double")]
    public async Task Double_arithmetic_is_unchanged(string source, string expected)
        => Assert.Equal(expected, await RunAsync($"echo $\"{{{source}}}\""));

    /// <summary>
    /// Widening the literal made a second defect reachable, and it is fixed rather than
    /// filed — `TOAST-0026`.
    /// </summary>
    /// <remarks>
    /// A `decimal` against a `double` was decided by conversion, and converting the
    /// decimal to a double drops exactly the digit that distinguishes them. So
    /// `1.0000000000000001 == 1.0` was **true** while the same pair were correctly
    /// *different keys* — equality and key equality disagreeing, and `==` intransitive
    /// again: `x == 1.0` and `1.0 == 1` held while `x == 1` did not.
    ///
    /// The rule is the one already decided for integers against floats, extended: the
    /// floating value is taken at its round-trip form and read back as a decimal. That
    /// keeps `0.1 as decimal == 0.1` true, which comparing exact binary values would not —
    /// no `double` is exactly a tenth.
    /// </remarks>
    [Fact]
    public async Task Equality_is_transitive_across_decimal_and_double()
    {
        Assert.Equal("False", await RunAsync("(1.0000000000000001 == 1.0)"));
        Assert.Equal("False", await RunAsync("(1.0 == 1.0000000000000001)"));
        Assert.Equal("False", await RunAsync("(1.0000000000000001 == 1)"));
        Assert.Equal("True", await RunAsync("(1.0 == 1)"));

        // The sane cases stay sane.
        Assert.Equal("True", await RunAsync("((0.1 as decimal) == 0.1)"));
        Assert.Equal("True", await RunAsync("((1.5 as decimal) == 1.5)"));

        // And equality now agrees with key equality, which it did not.
        Assert.Equal("3", await RunAsync("[1.0000000000000001, 1.0, \"z\"] | distinct | count"));
    }

    /// <summary>
    /// A widened literal mixes with doubles rather than standing apart.
    /// </summary>
    /// <remarks>
    /// The reason the wart is tolerable: the two numeric types already interoperate, so a
    /// literal changing type does not strand it. `decimal + double` yields a `Decimal`,
    /// and comparison and rendering work either way.
    /// </remarks>
    [Theory]
    [InlineData("((1.0000000000000001 + 1.5).GetType().Name)", "Decimal")]
    [InlineData("(1.0000000000000001 < 2.0)", "True")]
    [InlineData("(1.0000000000000001 > 1.0)", "True")]
    public async Task A_widened_literal_mixes_with_doubles(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));
}
