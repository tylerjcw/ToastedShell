using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A numeric string converts exactly as the number it spells, and a refusal
/// caused by fraction loss says so.
///
/// `TS-P2-111`. Refusing a lossy narrowing is deliberate — `TypeConversion`
/// guards it explicitly — and this does not change that. Two things around it
/// were wrong. The **spelling decided the outcome**: `7.0 as int` was 7 while
/// `"7.0" as int` failed, because a string never reached the guard at all —
/// `Convert.ChangeType` simply cannot parse `"7.0"` as an integer, so the
/// refusal was incidental rather than policy. And the **message explained
/// nothing**: `Cannot convert 'Double' to 'int'` reads as "this type never
/// converts", when the identical cast succeeds for a whole-valued double.
/// </summary>
public class NarrowingConversionTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return string.Join(",", results.Select(value => value?.ToString() ?? "null"));
    }

    private static async Task<string> ErrorOf(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => engine.ExecuteToListAsync(source));
        return exception.Message;
    }

    /// <summary>A whole-valued number narrows; this always worked and must stay.</summary>
    [Theory]
    [InlineData("7.0 as int", "7")]
    [InlineData("(-7.0) as int", "-7")]
    [InlineData("7.0 as long", "7")]
    public async Task A_whole_valued_number_narrows(string expression, string expected)
        => Assert.Equal(expected, await RunAsync(expression));

    /// <summary>
    /// The fix: the same value spelled as a string behaves the same way.
    /// </summary>
    [Theory]
    [InlineData("\"7.0\" as int", "7")]
    [InlineData("\"-7.0\" as int", "-7")]
    [InlineData("\"7\" as int", "7")]
    [InlineData("\"1e3\" as int", "1000")]
    public async Task A_numeric_string_narrows_like_the_number_it_spells(string expression, string expected)
        => Assert.Equal(expected, await RunAsync(expression));

    /// <summary>
    /// The probe parses through `decimal`, not `double`, and this is the case
    /// that decides it: 2^53+1 is the smallest integer a `double` cannot
    /// represent, so a double probe would convert 9007199254740993 to
    /// 9007199254740992 — a value the caller never wrote, silently.
    /// </summary>
    [Theory]
    [InlineData("\"9007199254740993\" as long", "9007199254740993")]
    [InlineData("\"9223372036854775807\" as long", "9223372036854775807")]
    public async Task A_large_numeric_string_keeps_every_digit(string expression, string expected)
        => Assert.Equal(expected, await RunAsync(expression));

    /// <summary>
    /// The refusal stands — this is not a change of policy — but in both
    /// spellings, so the two agree on rejection as well as acceptance.
    /// </summary>
    [Theory]
    [InlineData("7.9 as int")]
    [InlineData("\"7.9\" as int")]
    [InlineData("cast int 7.9")]
    public async Task A_fractional_value_is_still_refused(string source)
        => Assert.Contains("fractional part", await ErrorOf(source));

    /// <summary>
    /// And the message names the remedy. Without this the reader is told a
    /// `Double` cannot become an `int`, which is not true and suggests nothing.
    /// </summary>
    [Fact]
    public async Task The_refusal_names_a_rounding_call()
    {
        var message = await ErrorOf("7.9 as int");

        Assert.Contains("Math.Round", message);
        Assert.Contains("Math.Truncate", message);
    }

    /// <summary>
    /// The negative control, and the reason the two failures are kept apart: a
    /// value that is not a number at all must still get the type-mismatch
    /// message, not advice about rounding it.
    /// </summary>
    [Fact]
    public async Task An_unrelated_value_still_reports_a_plain_mismatch()
    {
        var message = await ErrorOf("\"abc\" as int");

        Assert.Contains("Cannot convert", message);
        Assert.DoesNotContain("fractional part", message);
    }

    /// <summary>
    /// Annotations take the same conversion path, so they gained the same parity
    /// and the same message — from one shared helper, because the text had been
    /// written out twice.
    /// </summary>
    [Theory]
    [InlineData("var x: int = \"7.0\"\n$x", "7")]
    [InlineData("var x: long = \"9007199254740993\"\n$x", "9007199254740993")]
    public async Task An_annotation_accepts_a_numeric_string_the_same_way(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    [Theory]
    [InlineData("var x: int = 7.9")]
    [InlineData("var x: int = \"7.9\"")]
    public async Task An_annotation_refusing_a_fraction_says_so(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync(source));

        var diagnostic = Assert.Single(exception.Diagnostics);

        Assert.Contains("fractional part", diagnostic.Title);

        // The remedy lives on the label rather than the title, which is where the
        // renderer shows it and where the reader looks after the underline.
        Assert.Contains("Math.Round", diagnostic.Label);
        Assert.Contains("Math.Truncate", diagnostic.Label);
    }
}
