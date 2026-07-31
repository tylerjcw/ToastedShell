using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Introspection on a <c>ShellTextLine</c> reports the surface that is actually callable —
/// <c>TS-P1-33</c>.
/// </summary>
/// <remarks>
/// <para>
/// `members` listed exactly one member, `Text`, while every `string` member worked on the value:
/// `.Trim()`, `.Length`, `.ToUpper()`, `.Split()`, `== "…"`, `cast string`, and a `string`
/// annotation. `ReflectionObjectAccessor` unwraps to the underlying string, so behaviour was
/// right all along and only the description was wrong.
/// </para>
/// <para>
/// That mismatch is not cosmetic — it caused a wrong diagnosis in this programme. Seeing one
/// member and no string surface convinced both a reporter and the author that `ShellTextLine`
/// was not string-like, which produced a plan to add conversions that already existed. Two
/// people read the introspection, believed it over the behaviour, and were wrong.
/// </para>
/// <para>
/// The fix is one case in `ReflectionMetadataUtilities.ResolveTypeLikeTarget`, which is shared
/// by `members`, `methods`, `props`, `funcs`, `constructors` and `describe-type` — so all six
/// agree rather than six commands each learning about the wrapper.
/// </para>
/// </remarks>
public sealed class TextLineIntrospectionTests
{
    private static async Task<object?> EvaluateAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? null : results[^1];
    }

    /// <summary>A genuine <c>ShellTextLine</c>, produced the way the reporter's was.</summary>
    private const string TextLine = "var x = ({| a = 1 |} | to json)\n";

    [Fact]
    public async Task The_value_really_is_a_text_line()
    {
        // Guards the premise. If `to json` ever stopped producing a ShellTextLine, every other
        // assertion here would pass while testing nothing.
        Assert.Equal("ShellTextLine", await EvaluateAsync($"{TextLine}($x | type-of).Name"));
    }

    [Fact]
    public async Task Methods_reports_the_whole_string_surface()
    {
        // The headline: it listed one member. It now reports what a string reports, by count,
        // compared against the type itself rather than against a number that would silently
        // drift with the framework.
        var textLine = await EvaluateAsync($"{TextLine}($x | methods | count)");
        var stringType = await EvaluateAsync("(\"System.String\" | methods | count)");

        Assert.Equal(stringType, textLine);
        Assert.True(Convert.ToInt32(textLine) > 50, $"expected the string surface, got {textLine}");
    }

    [Theory]
    // Spot-checks by name, so a count that matched for the wrong reason would still be caught.
    [InlineData("Trim")]
    [InlineData("ToUpper")]
    [InlineData("Split")]
    [InlineData("Contains")]
    public async Task A_string_method_is_discoverable_by_name(string method)
    {
        // At least one, not exactly one: these are overload sets — `Trim` reports 4 and `Split`
        // 11. Asserting equality with 1 failed here first, which is the listing being more
        // faithful than the assertion was.
        var count = Convert.ToInt32(await EvaluateAsync(
            $"{TextLine}($x | methods | where {{ $_.Name == \"{method}\" }} | count | first)"));

        Assert.True(count >= 1, $"'{method}' is callable on a text line but was not listed");
    }

    [Fact]
    public async Task Members_reports_string_properties_rather_than_the_wrapper()
    {
        Assert.Equal(1, await EvaluateAsync(
            $"{TextLine}($x | members | where {{ $_.Name == \"Length\" }} | count | first)"));

        // `Text` was the *only* thing listed before, and it was the whole problem: it implied
        // the value had to be unwrapped to be used.
        Assert.Equal(0, await EvaluateAsync(
            $"{TextLine}($x | members | where {{ $_.Name == \"Text\" }} | count | first)"));
    }

    [Theory]
    // Behaviour is unchanged — this only ever described the value differently. `.Text` keeps
    // working even though it is no longer advertised, so nothing that relied on it breaks.
    [InlineData("$x.Length", 12)]
    [InlineData("$x.Text.Length", 12)]
    public async Task Access_is_unchanged(string expression, int expected)
    {
        Assert.Equal(expected, await EvaluateAsync($"{TextLine}{expression}"));
    }

    [Fact]
    public async Task A_plain_string_is_still_read_as_a_type_name()
    {
        // The asymmetry this fix creates, pinned deliberately rather than discovered later: for
        // a `string` these commands treat the value as the *name of a type*, which is what makes
        // `"System.String" | methods` work and `"hello" | members` fail. A ShellTextLine is raw
        // text from a command, where a type name is nearly never what was meant — so the two are
        // treated differently on purpose.
        Assert.True(Convert.ToInt32(await EvaluateAsync("(\"System.String\" | methods | count)")) > 50);

        await Assert.ThrowsAnyAsync<Exception>(
            async () => await EvaluateAsync("(\"hello\" | members | count)"));
    }
}
