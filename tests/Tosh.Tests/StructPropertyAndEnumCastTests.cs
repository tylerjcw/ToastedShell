using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Two defects found while nesting types, neither caused by nesting.
/// </summary>
/// <remarks>
/// <para>
/// <b>A struct's declared properties were parsed and forgotten.</b> <c>Properties</c> was stored
/// on the definition and read by nothing at all: <c>CreateInstance</c> initialised only the fields
/// a primary constructor declares. So <c>struct Point { prop X = 1 }</c> produced an instance with
/// no members — <c>$p.X</c> failed and <c>$p | members</c> counted zero — while
/// <c>struct Point(x) { … }</c> worked, because those are fields. The nesting work surfaced it:
/// <c>struct</c> is one of the keywords that may now be nested, and a nested struct was as
/// unusable as an outer one.
/// </para>
/// <para>
/// The listing had to be fixed in two places rather than one. Member *access* reads the instance's
/// stored values, but <c>members</c> reads the type descriptor, and both described fields alone —
/// introspection contradicting behaviour, which is the <c>TS-P1-33</c> shape.
/// </para>
/// <para>
/// <b>An enum converted through its name rather than its value.</b> <c>cast int Fuel.Uranium</c>
/// reported "Could not cast 'Uranium' to System.Int32": with no case for an enum, the value fell
/// through to the string conversions, and <c>ToString</c> on an enum is its name. Conversion now
/// goes through the underlying value — except to <c>string</c>, where the name is the right answer.
/// </para>
/// </remarks>
public sealed class StructPropertyAndEnumCastTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return string.Join(",", results.Select(value => value?.ToString() ?? "null"));
    }

    // ── A struct's properties exist ────────────────────────────────────────────

    [Fact]
    public async Task A_declared_property_is_readable_on_a_struct_instance()
    {
        Assert.Equal("1", await RunAsync("struct Point { prop X = 1 }\n(new Point()).X"));
    }

    [Fact]
    public async Task Several_properties_are_each_initialised()
    {
        Assert.Equal("1,2", await RunAsync(
            """
            struct Point {
                prop X = 1
                prop Y = 2
            }
            var pt = new Point()
            $pt.X
            $pt.Y
            """));
    }

    [Fact]
    public async Task A_property_initialiser_may_read_a_primary_constructor_parameter()
    {
        // The bound fields serve as locals, as they do for a class initialiser.
        Assert.Equal("8", await RunAsync("struct P(x: int) { prop Doubled = ($x * 2) }\n(new P(4)).Doubled"));
    }

    [Theory]
    // Both listings: the instance's own members, and what the type descriptor reports.
    [InlineData("struct Point { prop X = 1\n    prop Y = 2 }\n((new Point()) | members | count)", "2")]
    [InlineData("struct P(x: int) { prop Doubled = ($x * 2) }\n((new P(4)) | members | count)", "2")]
    public async Task Properties_appear_in_the_member_listing(string source, string expected)
    {
        Assert.Equal(expected, await RunAsync(source));
    }

    [Theory]
    // The controls: the primary-constructor form always worked, and a struct with nothing
    // declared must still construct.
    [InlineData("struct Point(x: int) { prop X = $x }\n(new Point(5)).X", "5")]
    [InlineData("struct Empty { }\nvar e = new Empty()\n\"ok\"", "ok")]
    public async Task Struct_forms_that_already_worked_are_unchanged(string source, string expected)
    {
        Assert.Equal(expected, await RunAsync(source));
    }

    [Fact]
    public async Task A_nested_struct_is_usable_like_any_other()
    {
        // What tied this to the nesting work: `struct` may be nested, and a nested one inherited
        // exactly this defect.
        Assert.Equal("7", await RunAsync(
            "class Outer { struct Point { prop X = 7 } }\n(new Outer.Point()).X"));
    }

    // ── An enum converts through its value ─────────────────────────────────────

    [Theory]
    [InlineData("enum Fuel : int { Uranium = 8 }\nvar v = Fuel.Uranium\ncast int $v", "8")]
    [InlineData("class R { enum Fuel : int { Uranium = 8 } }\nvar v = R.Fuel.Uranium\ncast int $v", "8")]
    public async Task An_enum_casts_to_its_underlying_number(string source, string expected)
    {
        Assert.Equal(expected, await RunAsync(source));
    }

    [Fact]
    public async Task An_enum_still_casts_to_its_name_as_a_string()
    {
        // The exception that keeps the change honest: to `string`, the name is what is wanted,
        // and routing every conversion through the underlying value would have lost it.
        Assert.Equal("Uranium", await RunAsync(
            "enum Fuel : int { Uranium = 8 }\nvar v = Fuel.Uranium\ncast string $v"));
    }

    [Fact]
    public async Task An_enum_still_displays_as_its_name()
    {
        Assert.Equal("Uranium", await RunAsync("enum Fuel : int { Uranium = 8 }\nFuel.Uranium"));
    }

    [Fact]
    public async Task An_enum_of_another_underlying_type_converts_too()
    {
        Assert.Equal("300", await RunAsync(
            "enum Big : long { Huge = 300 }\nvar v = Big.Huge\ncast long $v"));
    }
}
