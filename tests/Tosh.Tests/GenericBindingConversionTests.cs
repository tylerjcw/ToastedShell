using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// What a value bound to a type parameter is checked against — <c>TOAST-0124</c>,
/// and the `any` alias from the <c>TOAST-0125</c> audit.
/// </summary>
/// <remarks>
/// <para>
/// The check accepted only an exact type, so `new Vector2D&lt;double&gt;(0, 0)` was refused:
/// `0` is an Int32 and nothing is lost by widening it. A generic factory could not be
/// written at all, because there is no way to spell "zero of T" when the literal must
/// already be the right type.
/// </para>
/// <para>
/// The strictness is kept — it is what stops a `Point2D&lt;int&gt;` taking 3.5 — and only the
/// direction is now distinguished. The permitted set is exactly C#'s implicit numeric
/// conversions, deliberately not the general converter, which also parses strings and
/// truncates doubles and would take the 3.5.
/// </para>
/// </remarks>
public sealed class GenericBindingConversionTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? string.Empty : results[^1]?.ToString() ?? "null";
    }

    private const string Box = "class Box<T>(v: T) { prop V: T = $v }\n";

    [Theory]
    [InlineData("int", "double", "0", "Double")]
    [InlineData("int", "long", "1", "Int64")]
    [InlineData("int", "decimal", "2", "Decimal")]
    [InlineData("int", "float", "3", "Single")]
    public async Task A_lossless_widening_is_converted_not_merely_permitted(
        string _,
        string parameter,
        string literal,
        string expected)
    {
        // Converted: the stored value must *be* the wider type, or the declared type is a
        // lie of a different shape from the one being prevented.
        var source = $"{Box}var b = new Box<{parameter}>({literal})\n(type-of $b.V).Name";
        Assert.Equal(expected, await RunAsync(source));
    }

    [Theory]
    [InlineData("new Box<int>(3.5)")]          // narrowing, and lossy
    [InlineData("new Box<int>(\"a\")")]        // not numeric at all
    [InlineData("new Box<double>(\"a\")")]
    [InlineData("new Box<long>(1.5)")]
    public async Task A_narrowing_or_unrelated_value_is_still_refused(string construction)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync($"{Box}{construction}"));
        Assert.Contains("could not be converted", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_generic_factory_can_be_written()
    {
        // The case the library needed and could not express: `0` is right when T is int and
        // wrong when it is double, and there is no way to say "zero of T".
        var source =
            "class Box<T>(v: T) {\n"
            + "    prop V: T = $v\n"
            + "    static func Zero<U>() -> Box<U> { return (new Box<U>(0)) }\n"
            + "}\n"
            + "var a = Box.Zero<double>()\n"
            + "var b = Box.Zero<int>()\n"
            + "echo $\"{(type-of $a.V).Name}/{(type-of $b.V).Name}\"";

        Assert.Equal("Double/Int32", await RunAsync(source));
    }

    [Theory]
    // The spec names `dynamic`, `any` and `object` as synonyms for explicit dynamic. `any`
    // had no alias entry, so it fell through to a general CLR search and found
    // `System.Runtime.InteropServices.JavaScript.JSType+Any` — which no value can convert to.
    [InlineData("var x: any = 5\n$x", "5")]
    [InlineData("func f(v: any) { return $v }\nf 7", "7")]
    [InlineData("func g() -> any { return 9 }\ng", "9")]
    [InlineData("class C(v: any) { prop V: any = $v }\n(new C(4)).V", "4")]
    [InlineData("class B<T>(v: T) { prop V: T = $v }\n(new B<any>(6)).V", "6")]
    public async Task Any_is_a_synonym_for_dynamic_in_every_position(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    [Theory]
    // …and still means the same as the two spellings that already worked.
    [InlineData("var x: dynamic = 5\n$x", "5")]
    [InlineData("var x: object = 5\n$x", "5")]
    public async Task The_other_two_spellings_are_unchanged(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));
}
