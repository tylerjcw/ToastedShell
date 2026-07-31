using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// An object-keyed dictionary yields its fields instead of crashing — <c>TS-P1-29</c>.
/// </summary>
/// <remarks>
/// <para>
/// `ShellRecordUtilities.TryGetFields` iterated an `IDictionary` with `Cast&lt;DictionaryEntry&gt;()`.
/// That enumerates through `IEnumerable`, where `Dictionary&lt;K,V&gt;` yields boxed
/// `KeyValuePair&lt;K,V&gt;` — only its explicit `IDictionary.GetEnumerator()` yields
/// `DictionaryEntry`. A `{% … %}` literal is object-keyed, so `{% "a" =&gt; 1 %} | to json` died
/// with a raw `InvalidCastException` surfacing as `unexpected_exception`: no diagnostic, no span,
/// just a CLR type name.
/// </para>
/// <para>
/// The utility is called by the format converters and by equality, so the blast radius was wider
/// than one command — and it is why `TS-P1-10` had to be narrowed to string-keyed records to
/// avoid tripping over it.
/// </para>
/// </remarks>
public sealed class DictionaryFieldsTests
{
    private static async Task<object?> EvaluateAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? null : results[^1];
    }

    [Theory]
    // The reported crash, and the same shape through every converter that shares the utility.
    [InlineData("""{% "a" => 1, "b" => 2 %} | to json""", "\"a\": 1")]
    [InlineData("""{% "a" => 1, "b" => 2 %} | to csv""", "a,b")]
    [InlineData("""{% "a" => 1, "b" => 2 %} | to xml""", "<a>1</a>")]
    public async Task A_dictionary_converts_without_crashing(string source, string expected)
    {
        var result = await EvaluateAsync(source);

        Assert.Contains(expected, Convert.ToString(result) ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_non_string_keyed_dictionary_converts_too()
    {
        // The literal is object-keyed whatever the keys look like, but integer keys make the
        // point unmistakable: the fix is about the *enumerator shape*, not about key types.
        var result = Convert.ToString(await EvaluateAsync("""{% 1 => "one", 2 => "two" %} | to json""")) ?? "";

        Assert.Contains("\"1\": \"one\"", result, StringComparison.Ordinal);
        Assert.Contains("\"2\": \"two\"", result, StringComparison.Ordinal);
    }

    [Fact]
    public void TryGetFields_handles_both_enumerator_shapes()
    {
        // Directly against the utility, because the two dictionary types differ in exactly the
        // way that broke it: Hashtable's IEnumerable yields DictionaryEntry, and
        // Dictionary<object, object?>'s yields KeyValuePair<object, object?>. Only the first
        // worked before. A test that used just one of them would have proved nothing.
        var generic = new Dictionary<object, object?> { ["a"] = 1, [2] = "two" };
        var legacy = new System.Collections.Hashtable { ["a"] = 1, [2] = "two" };

        Assert.True(ShellRecordUtilities.TryGetFields(generic, out var genericFields));
        Assert.True(ShellRecordUtilities.TryGetFields(legacy, out var legacyFields));

        Assert.Equal(2, genericFields.Count);
        Assert.Equal(2, legacyFields.Count);
        Assert.Equal(
            genericFields.OrderBy(field => field.Key, StringComparer.Ordinal).Select(field => field.Key),
            legacyFields.OrderBy(field => field.Key, StringComparer.Ordinal).Select(field => field.Key));
    }

    [Fact]
    public async Task A_record_is_unaffected()
    {
        // The branch above the dictionary one, which always worked — asserted so a change to the
        // dictionary case cannot quietly reroute records through it.
        Assert.Contains(
            "\"a\": 1",
            Convert.ToString(await EvaluateAsync("{| a = 1 |} | to json")) ?? string.Empty,
            StringComparison.Ordinal);
    }
}
