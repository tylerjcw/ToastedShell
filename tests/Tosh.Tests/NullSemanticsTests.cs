using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// What `null` means, as §What null Means states it — `TOAST-0018`.
/// </summary>
/// <remarks>
/// <para>
/// Equality, ordering and key equality each had to decide what `null` meant and each
/// decided separately: equal only to itself, outside the order, its own key. This is the
/// rest — truthiness, arithmetic, reaching into a null, and membership — and the rule that
/// unifies them: an operation with no sensible answer for a missing value reports that,
/// and the author asks for propagation where they want it.
/// </para>
/// <para>
/// Three behaviours were changed rather than written down. Reading a member of `null`
/// answered `null` silently for *any* member name, so a misspelling reported nothing on a
/// null while raising on a string — and that left `?.` meaning nothing, since both
/// spellings behaved identically though `?.` is documented as yielding null "instead of
/// failing". `null + "a"` was `"a"`, so a missing value vanished into concatenated output
/// while `null + 1` raised. And `"abc" contains null` was true, because `null` rendered as
/// the empty string and every string contains that.
/// </para>
/// </remarks>
public sealed class NullSemanticsTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? string.Empty : results[^1]?.ToString() ?? "null";
    }

    private static async Task<Exception> ThrowsAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        return await Assert.ThrowsAnyAsync<Exception>(() => engine.ExecuteToListAsync(source));
    }

    /// <summary>`null` is falsy.</summary>
    [Theory]
    [InlineData("var x = null\nif ($x) { \"truthy\" } else { \"falsy\" }", "falsy")]
    [InlineData("(not null)", "True")]
    [InlineData("(null ?? \"fallback\")", "fallback")]
    [InlineData("(\"set\" ?? \"fallback\")", "set")]
    public async Task Null_is_falsy(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// The three relations each say something different about `null`, and all three are
    /// deliberate.
    /// </summary>
    /// <remarks>
    /// Equal only to itself; outside the order entirely, so an ordered comparison is false
    /// in every direction; and its own key. Pinned together because the differences are
    /// the kind that get "tidied" into agreement by someone who has not read why.
    /// </remarks>
    [Fact]
    public async Task The_three_relations_treat_null_differently_on_purpose()
    {
        Assert.Equal("True", await RunAsync("(null == null)"));
        Assert.Equal("False", await RunAsync("(null == 0)"));

        // Outside the order: not less, not greater, not "equal" either.
        Assert.Equal("False", await RunAsync("(null < null)"));
        Assert.Equal("False", await RunAsync("(null >= null)"));

        // Its own key, and not the key of anything else.
        Assert.True(ShellKeyComparer.Instance.Equals(null, null));
        Assert.False(ShellKeyComparer.Instance.Equals(null, 0));
    }

    /// <summary>
    /// Arithmetic raises, including against a string.
    /// </summary>
    /// <remarks>
    /// `null + "a"` was the odd one out at `"a"`. A missing value turning into empty text
    /// is how a null reaches a log line, a filename or a command argument without anyone
    /// noticing it was missing.
    /// </remarks>
    [Theory]
    [InlineData("(null + 1)")]
    [InlineData("(1 + null)")]
    [InlineData("(null - 1)")]
    [InlineData("(null * 2)")]
    [InlineData("(null + \"a\")")]
    [InlineData("(\"a\" + null)")]
    public async Task Arithmetic_on_null_raises(string source)
        => Assert.Contains("non-null", (await ThrowsAsync(source)).Message, StringComparison.OrdinalIgnoreCase);

    /// <summary>Ordinary arithmetic and concatenation are untouched.</summary>
    [Theory]
    [InlineData("(\"a\" + \"b\")", "ab")]
    [InlineData("(\"a\" + 1)", "a1")]
    [InlineData("(1 + 2)", "3")]
    [InlineData("((null ?? \"\") + \"a\")", "a")]
    public async Task Arithmetic_without_null_is_unchanged(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// Reaching into `null` raises, whichever way it is spelled.
    /// </summary>
    [Theory]
    [InlineData("var x = null\n$x.Length", "Cannot read member")]
    [InlineData("var x = null\n$x.Anything", "Cannot read member")]
    [InlineData("var x = null\n$x.ToString()", "Cannot invoke")]
    [InlineData("var x = null\n$x[0]", "Cannot index into null")]
    public async Task Reaching_into_null_raises(string source, string expectedMessage)
        => Assert.Contains(expectedMessage, (await ThrowsAsync(source)).Message, StringComparison.Ordinal);

    /// <summary>
    /// `?.` is how propagation is asked for, and now it is the only way.
    /// </summary>
    /// <remarks>
    /// This is the test that would have failed before the change in the other direction:
    /// `.` and `?.` both answered `null`, so nothing distinguished them.
    /// </remarks>
    [Fact]
    public async Task The_null_safe_operator_is_what_propagates()
    {
        // Observed through interpolation on purpose: a bare `null` expression yields no
        // result at all, so asserting on the statement's value cannot tell "propagated a
        // null" from "produced nothing".
        Assert.Equal("[null]", await RunAsync("var x = null\necho $\"[{$x?.Length}]\""));

        // And the plain spelling does not, which is what makes `?.` mean something.
        await ThrowsAsync("var x = null\n$x.Length");
    }

    /// <summary>A misspelled member raises on a null receiver as it does on any other.</summary>
    /// <remarks>
    /// The practical reason for the change. `$x.Lenght` reported nothing when `$x` was
    /// null and raised when it was a string, so the same typo was silent or loud depending
    /// on data.
    /// </remarks>
    [Fact]
    public async Task A_misspelled_member_raises_whether_or_not_the_receiver_is_null()
    {
        Assert.Contains("Lenght", (await ThrowsAsync("var x = null\n$x.Lenght")).Message, StringComparison.Ordinal);
        Assert.Contains("Lenght", (await ThrowsAsync("var x = \"abc\"\n$x.Lenght")).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A collection can hold `null` and be asked about it; a string cannot contain it.
    /// </summary>
    /// <remarks>
    /// The two halves are a different question, which is why only one of them moved.
    /// Membership asks whether an element is present, and `null` is a perfectly good
    /// element. `contains` on a string asks whether some text appears in it, and `null` is
    /// not text — it was answering true only because it rendered as `""`.
    /// </remarks>
    [Theory]
    [InlineData("(null in [1, null])", "True")]
    [InlineData("(null in [1, 2])", "False")]
    [InlineData("([1, null] contains null)", "True")]
    [InlineData("(\"abc\" contains null)", "False")]
    [InlineData("(\"abc\" contains \"b\")", "True")]
    [InlineData("(\"abc\" contains \"\")", "True")]
    public async Task Membership_distinguishes_an_element_from_text(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// Both equality implementations agree about `contains`.
    /// </summary>
    /// <remarks>
    /// `OperatorEvaluator.Contains` and `ToshEngine.ContainsAsync` are another parallel
    /// pair, and the engine's is the one a script reaches — the same shape that let the
    /// exact-numeric fix land on the wrong half.
    /// </remarks>
    [Fact]
    public async Task Both_contains_implementations_agree_about_null()
    {
        Assert.Equal("False", await RunAsync("(\"abc\" contains null)"));
        Assert.Equal(false, OperatorEvaluator.EvaluateBinary("abc", "contains", null));
    }
}
