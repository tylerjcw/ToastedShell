using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// What a `str` is made of, as §Text and Unicode states it — `TOAST-0018`.
/// </summary>
/// <remarks>
/// <para>
/// The concern `TOAST-0014` deferred here: what a `str` is made of, what `Length` counts,
/// and how indexing, slicing and comparison behave. The decision was to **keep UTF-16 code
/// units** — the model the runtime already had — and to leave normalisation explicit.
/// Nothing changed; this corpus is what makes "unchanged" mean something a backend can be
/// held to.
/// </para>
/// <para>
/// Written with `\uHHHH` escapes rather than literal characters throughout, which is both
/// the clearer spelling for a file about code units and the only one the specification's
/// `lstlisting` blocks can typeset.
/// </para>
/// <para>
/// **The escapes must be in `$'...'` strings.** A double-quoted string takes `\n`, `\t`
/// and the other control escapes but *not* `\uHHHH` — it keeps those six characters as
/// text, silently. The specification said otherwise until this corpus was run against it:
/// the `-c` command line had been doing shell-level escape processing, so every probe
/// through `tosh -c` appeared to prove an escape the language does not have.
/// </para>
/// </remarks>
public sealed class UnicodeSemanticsTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? string.Empty : results[^1]?.ToString() ?? "null";
    }

    /// <summary>`Length` counts UTF-16 code units, not characters.</summary>
    /// <remarks>
    /// The waving hand is one character and two code units; the family is three people and
    /// two joiners, so eight. A grapheme-cluster model would answer 1 to both, and a
    /// scalar-value model 1 and 5.
    /// </remarks>
    [Theory]
    [InlineData("\"abc\"", "3")]
    [InlineData("$'\\u00E9'", "1")]                                       // precomposed
    [InlineData("$'e\\u0301'", "2")]                                      // e + combining acute
    [InlineData("$'\\uD83D\\uDC4B'", "2")]                                // waving hand
    [InlineData("$'\\uD83D\\uDC68\\u200D\\uD83D\\uDC69\\u200D\\uD83D\\uDC67'", "8")]  // family
    public async Task Length_counts_code_units(string literal, string expected)
        => Assert.Equal(expected, await RunAsync($"({literal}.Length)"));

    /// <summary>
    /// Indexing reaches a code unit, so it can return half a character.
    /// </summary>
    /// <remarks>
    /// The sharp edge, pinned deliberately rather than tolerated quietly. `$w[1]` is a
    /// high surrogate and `$w[2]` a low one: each is a valid `Char` and neither is valid
    /// text on its own. A specification that claims UTF-16 code units has to own this.
    /// </remarks>
    [Fact]
    public async Task Indexing_can_return_half_a_character()
    {
        const string Setup = "var w = $'a\\uD83D\\uDC4Bb'\n";

        Assert.Equal("4", await RunAsync(Setup + "($w.Length)"));
        Assert.Equal("a", await RunAsync(Setup + "($w[0])"));
        Assert.Equal("b", await RunAsync(Setup + "($w[3])"));

        // The two halves: each a `Char`, neither a character.
        Assert.Equal("Char", await RunAsync(Setup + "($w[1].GetType().Name)"));
        Assert.Equal('\uD83D'.ToString(), await RunAsync(Setup + "($w[1])"));
        Assert.Equal('\uDC4B'.ToString(), await RunAsync(Setup + "($w[2])"));
    }

    /// <summary>
    /// Comparison is exact and does not normalise.
    /// </summary>
    /// <remarks>
    /// Two spellings of the same character, rendering identically, are not equal — and are
    /// not the same container key either, which is what makes the macOS/Linux filename
    /// case a real trap rather than a curiosity.
    /// </remarks>
    [Fact]
    public async Task Canonically_equivalent_strings_are_not_equal()
    {
        Assert.Equal("False", await RunAsync("($'e\\u0301' == $'\\u00E9')"));

        // Nor the same key, so a `distinct` keeps both. The sentinel keeps `count`
        // measuring items rather than the survivor's contents.
        Assert.Equal("3", await RunAsync("[$'e\\u0301', $'\\u00E9', \"z\"] | distinct | count"));

        Assert.False(ShellKeyComparer.Instance.Equals("e\u0301", "\u00E9"));
    }

    /// <summary>Normalisation is available, and explicit.</summary>
    [Theory]
    [InlineData("($'e\\u0301'.Normalize() == $'\\u00E9'.Normalize())", "True")]
    [InlineData("($'e\\u0301'.Normalize().Length)", "1")]
    [InlineData("($'\\u00E9'.IsNormalized())", "True")]
    [InlineData("($'e\\u0301'.IsNormalized())", "False")]
    public async Task Normalisation_is_available_explicitly(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// String ordering is by code unit, which is what makes it portable.
    /// </summary>
    /// <remarks>
    /// Settled by §Ordering; repeated here on characters where a culture would disagree.
    /// Under a Swedish collation `"z" &lt; "ä"` is true and under an American one false;
    /// by code point it is true everywhere, because `z` is U+007A and `ä` is U+00E4.
    /// </remarks>
    [Theory]
    [InlineData("(\"z\" < $'\\u00E4')", "True")]
    [InlineData("($'\\u00E4' < \"z\")", "False")]
    [InlineData("(\"a\" < \"B\")", "False")]
    public async Task String_ordering_is_by_code_unit(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// A string is one value, so the pipeline never takes it apart.
    /// </summary>
    /// <remarks>
    /// The saving grace of the code-unit model: because a `str` is an atom rather than a
    /// collection of characters, `reverse` cannot scramble an astral character into
    /// unpaired surrogates. The damage indexing can do stays where an author asked for it.
    /// </remarks>
    [Fact]
    public async Task A_string_is_not_taken_apart_by_the_pipeline()
    {
        const string Astral = "$'a\\uD83D\\uDC4Bb'";

        Assert.Equal("a\uD83D\uDC4Bb", await RunAsync($"{Astral} | reverse"));
        Assert.Equal("a\uD83D\uDC4Bb", await RunAsync($"{Astral} | sort"));
    }
}
