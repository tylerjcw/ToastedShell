using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// <c>sort</c> compares by code point, and <c>-i</c> asks for case folding —
/// <c>TS-P2-75</c>, then <c>TOAST-0018</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The default turned over.</b> <c>sort</c> compared with
/// <see cref="StringComparer.OrdinalIgnoreCase"/>, which reads better — <c>Apple</c> and
/// <c>apple</c> belong next to each other — and had a consequence that surprises. Case
/// folding raises lowercase letters above <c>_</c>: uppercasing turns <c>s</c> (0x73) into
/// <c>S</c> (0x53), which sorts *below* <c>_</c> (0x5F), so
/// <c>expected_record_fields</c> came before <c>expected_record_field_default</c> while by
/// code point it comes after.
/// </para>
/// <para>
/// <c>TS-P2-75</c> answered that with an opt-*out* (<c>-o</c>). <c>TOAST-0018</c> turned it
/// into an opt-*in* (<c>-i</c>), for a reason the first pass did not weigh: a
/// case-insensitive order calls <c>"a"</c> and <c>"A"</c> **equal**, while <c>==</c> calls
/// them different. That is a broken trichotomy rather than a matter of taste — two values
/// neither less, nor greater, nor equal — and it put <c>sort</c> in contradiction with the
/// language's own ordering, which is by code point and portable.
/// </para>
/// <para>
/// <c>-o</c>/<c>--ordinal</c> is still accepted and now names the default, so scripts that
/// asked for code-point order keep getting it. Tests below use both spellings deliberately.
/// </para>
/// <para>
/// <b>Two of the three claims in this item's filing were wrong, and measurement is what
/// settled it.</b> I wrote that <c>sort</c> was culture-aware and therefore
/// locale-dependent: it is not, and comparing the failing pair shows
/// <c>CurrentCulture</c> and <c>InvariantCulture</c> both agreeing with <c>Ordinal</c>
/// (+1) while <c>OrdinalIgnoreCase</c> alone disagrees (-12). I also wrote that a block
/// key is evaluated per comparison: it is not — <c>SortCommand</c> materialises the keys
/// once per element before ordering. What survived is the part that mattered.
/// </para>
/// </remarks>
public sealed class SortOrdinalTests
{
    private static async Task<IReadOnlyList<string>> RunAsync(string script)
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);
        var results = await engine.ExecuteToListAsync(script);
        return results.Select(v => v?.ToString() ?? string.Empty).ToArray();
    }

    private const string Underscored =
        """var xs = ["expected_record_fields", "expected_record_field_default", "expected_record_field_name"]""";

    [Fact]
    public void The_comparer_that_causes_this_is_OrdinalIgnoreCase_not_culture()
    {
        // Pinned because the filing blamed culture. If this ever flips, the item's
        // explanation is wrong again and the next reader should find out here.
        const string a = "expected_record_fields";
        const string b = "expected_record_field_default";

        Assert.True(StringComparer.OrdinalIgnoreCase.Compare(a, b) < 0, "OrdinalIgnoreCase should place `fields` first");
        Assert.True(StringComparer.Ordinal.Compare(a, b) > 0, "Ordinal should place `field_default` first");
        Assert.True(StringComparer.InvariantCulture.Compare(a, b) > 0, "culture agrees with ordinal here");
    }

    [Fact]
    public async Task Ordinal_puts_underscore_before_letters()
    {
        var results = await RunAsync(Underscored + "\n$xs | sort --ordinal");

        Assert.Equal(
            ["expected_record_field_default", "expected_record_field_name", "expected_record_fields"],
            results);
    }

    [Fact]
    public async Task The_short_flag_is_the_same_thing()
    {
        Assert.Equal(
            await RunAsync(Underscored + "\n$xs | sort --ordinal"),
            await RunAsync(Underscored + "\n$xs | sort -o"));
    }

    [Fact]
    public async Task The_default_is_code_point_order()
    {
        // This asserted the opposite until 2026-08-19, pinning the case-insensitive
        // default as "deliberate and stays". It was deliberate; it stopped being right
        // when ordering had to agree with equality.
        var results = await RunAsync(Underscored + "\n$xs | sort");

        Assert.Equal(
            ["expected_record_field_default", "expected_record_field_name", "expected_record_fields"],
            results);
    }

    [Fact]
    public async Task The_default_and_the_ordinal_flag_are_now_the_same_thing()
        => Assert.Equal(
            await RunAsync(Underscored + "\n$xs | sort"),
            await RunAsync(Underscored + "\n$xs | sort -o"));

    [Fact]
    public async Task Case_insensitive_order_is_available_by_asking()
        => Assert.Equal(
            ["expected_record_fields", "expected_record_field_default", "expected_record_field_name"],
            await RunAsync(Underscored + "\n$xs | sort -i"));

    [Fact]
    public async Task Case_folding_is_what_the_flag_turns_on()
    {
        var byDefault = await RunAsync("""["b", "A", "a", "B"] | sort""");
        var insensitive = await RunAsync("""["b", "A", "a", "B"] | sort -i""");

        // Code point puts every capital before every lowercase; `-i` interleaves them.
        Assert.Equal(["A", "B", "a", "b"], byDefault);
        Assert.NotEqual(byDefault, insensitive);
    }

    [Fact]
    public async Task Unique_agrees_with_the_comparison_it_follows()
    {
        // `-u` deduplicates with the same case sensitivity as the sort, or `-u` would
        // fold `Alpha` and `alpha` together right after placing them apart.
        Assert.Equal(["Alpha", "alpha", "beta"], await RunAsync("""["Alpha", "alpha", "beta"] | sort -u"""));
        Assert.Equal(["Alpha", "beta"], await RunAsync("""["Alpha", "alpha", "beta"] | sort -u -i"""));
    }

    [Fact]
    public async Task It_composes_with_reverse_and_with_a_key()
    {
        Assert.Equal(
            ["expected_record_fields", "expected_record_field_name", "expected_record_field_default"],
            await RunAsync(Underscored + "\n$xs | sort -o -r"));

        Assert.Equal(
            ["bb", "aaa"],
            await RunAsync("""["aaa", "bb"] | sort -o { $_.Length }"""));
    }

    [Fact]
    public async Task Numeric_sorting_is_untouched()
    {
        Assert.Equal(["2", "10", "30"], await RunAsync("""["10", "2", "30"] | sort -n"""));
        Assert.Equal(["2", "10", "30"], await RunAsync("""["10", "2", "30"] | sort -n -o"""));
    }
}
