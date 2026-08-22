using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A coerced value has the refinement's base type — `TOAST-0068`.
/// </summary>
/// <remarks>
/// <para>
/// A refinement's coercer ran and its result was re-tested against the predicate, but never
/// converted back to the declared base type. A predicate tests the *value* and says nothing
/// about its type, so a coercer returning another numeric type was accepted:
/// </para>
/// <code>
/// type TimeoutMs = int where (_ &gt; 0 and _ &lt;= 300000) coerce Math.Clamp(_, 0, 300000)
///
/// var ok: TimeoutMs      = 500     // System.Int32
/// var coerced: TimeoutMs = 999999  // System.Double  ← declared `int`
/// </code>
/// <para>
/// Two values, one annotation, two CLR types — and only on the coerced path, so the ordinary
/// case looked right. The conversion algorithm is convert → test → coerce → **convert
/// again** → test; only the second conversion was missing.
/// </para>
/// </remarks>
public sealed class RefinementCoercionTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? string.Empty : results[^1]?.ToString() ?? "null";
    }

    private const string Timeout =
        "type TimeoutMs = int where (_ > 0 and _ <= 300000) coerce Math.Clamp(_, 0, 300000)\n";

    /// <summary>The coerced value is the base type, not whatever the coercer returned.</summary>
    /// <remarks>
    /// `Math.Clamp` resolves to an overload returning `double`. The assertion is on the CLR
    /// type rather than the number, because the number was always right.
    /// </remarks>
    [Fact]
    public async Task A_coerced_value_has_the_base_type()
        => Assert.Equal(
            "System.Int32",
            await RunAsync(Timeout + "var t: TimeoutMs = 999999\necho $t.GetType().FullName"));

    /// <summary>The uncoerced path is unchanged, which is what made this hard to see.</summary>
    [Fact]
    public async Task An_accepted_value_still_has_the_base_type()
        => Assert.Equal(
            "System.Int32",
            await RunAsync(Timeout + "var t: TimeoutMs = 500\necho $t.GetType().FullName"));

    /// <summary>Coercion still produces the coerced value.</summary>
    /// <remarks>
    /// The control for the two above: converting the result back must not discard it.
    /// </remarks>
    [Fact]
    public async Task Coercion_still_happens()
        => Assert.Equal("300000", await RunAsync(Timeout + "var t: TimeoutMs = 999999\necho $t"));

    /// <summary>
    /// A value the coercer cannot repair is still refused.
    /// </summary>
    /// <remarks>
    /// The post-coercion predicate test was already present and correct, and this asserts the
    /// extra conversion did not weaken it. `Math.Clamp(0, 0, 300000)` is `0`, which the
    /// predicate `_ > 0` rejects — a real defect in the library this was found in, where the
    /// coercer's lower bound should be `1`.
    /// </remarks>
    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public async Task A_value_the_coercer_cannot_repair_is_refused(string literal)
    {
        var failure = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => RunAsync(Timeout + $"var t: TimeoutMs = {literal}\necho $t"));

        Assert.Equal("tosh.runtime.refinement_failed", failure.Diagnostics[0].Code);
    }

    /// <summary>A float refinement keeps its own base type.</summary>
    /// <remarks>
    /// The counterpart: the fix must convert to the *declared* base, not to `int` in
    /// particular.
    /// </remarks>
    [Fact]
    public async Task A_float_refinement_coerces_to_its_own_base()
        => Assert.Equal(
            // `float` is `System.Single` here, which is the point: the conversion has to be
            // to the *declared* base rather than to whatever the coercer's overload returned.
            "System.Single",
            await RunAsync(
                "type UnitFloat = float where (_ >= 0.0 and _ <= 1.0) coerce Math.Clamp(_, 0.0, 1.0)\n" +
                "var u: UnitFloat = 4.5\necho $u.GetType().FullName"));

    /// <summary>
    /// A coercer whose result cannot become the base type is refused, not stored.
    /// </summary>
    /// <remarks>
    /// The case the conversion exists for: without it the slot would hold a string while
    /// claiming to be an `int`.
    /// </remarks>
    [Fact]
    public async Task A_coercer_returning_the_wrong_shape_is_refused()
    {
        var failure = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => RunAsync(
                "type Weird = int where _ > 100 coerce \"not a number\"\n" +
                "var w: Weird = 1\necho $w"));

        // Refused rather than stored, which is what matters. The code is
        // `expression_failed` rather than `refinement_failed` because the string never
        // becomes an `int` — asserted loosely on purpose, since which of the two a reader
        // would expect is a fair question and not one this item settles.
        Assert.StartsWith("tosh.runtime.", failure.Diagnostics[0].Code, StringComparison.Ordinal);
    }
}
