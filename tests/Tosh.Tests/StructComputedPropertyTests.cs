using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A computed property is evaluated on access, on a struct as on a class.
///
/// `TS-P2-85`. `struct S { prop Y =&gt; 7 }` then `(new S()).Y` reported "Member
/// 'Y' was not found on type 'S'". Construction skips computed properties when
/// seeding stored values, which is right — a computed property has no value until
/// it is read — but nothing evaluated them on access, so the value was simply
/// unreachable. `members` listed it all along, which made introspection advertise
/// a member that reading refused: the `TS-P1-33` shape.
///
/// The class form always worked, so every case here is asserted against both
/// kinds. A struct-only corpus would pass on a fix that made structs behave in
/// some third way.
/// </summary>
public class StructComputedPropertyTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return string.Join(",", results.Select(value => value?.ToString() ?? "null"));
    }

    /// <summary>
    /// The three body shapes the item names, each run on a struct and on the
    /// equivalent class.
    /// </summary>
    [Theory]
    [InlineData("struct", "Constant", "7")]
    [InlineData("class", "Constant", "7")]
    [InlineData("struct", "FromThis", "10")]
    [InlineData("class", "FromThis", "10")]
    [InlineData("struct", "Stored", "5")]
    [InlineData("class", "Stored", "5")]
    public async Task A_computed_property_reads_the_same_on_both_kinds(
        string kind,
        string member,
        string expected)
        => Assert.Equal(expected, await RunAsync(
            $$"""
            {{kind}} S {
                prop Stored = 5
                prop Constant => 7
                prop FromThis => ($this.Stored * 2)
            }
            (new S()).{{member}}
            """));

    /// <summary>
    /// Introspection and reading must agree, which is what the item was really
    /// about: the member was listed before the fix and reported `null`, beside a
    /// read that failed outright.
    /// </summary>
    [Fact]
    public async Task The_listing_carries_the_evaluated_value()
        => Assert.Equal("""{"Stored":5,"Doubled":10}""", await RunAsync(
            """
            struct S {
                prop Stored = 5
                prop Doubled => ($this.Stored * 2)
            }
            new S() | to json --compact
            """));

    /// <summary>
    /// A computed property is recomputed from current state rather than captured
    /// once. On a `fluid` struct the stored field can change, and the computed one
    /// must follow — a fix that cached at construction would pass every test above
    /// and fail this.
    /// </summary>
    [Fact]
    public async Task A_computed_property_follows_a_later_write()
        => Assert.Equal("10,14", await RunAsync(
            """
            fluid struct S {
                prop Stored = 5
                prop Doubled => ($this.Stored * 2)
            }
            var s = new S()
            $s.Doubled
            $s.Stored = 7
            $s.Doubled
            """));

    /// <summary>
    /// A stored property is untouched: the computed path is reached only when
    /// there is no stored value, so ordinary fields must not start taking it.
    /// </summary>
    [Fact]
    public async Task A_stored_property_is_unaffected()
        => Assert.Equal("5", await RunAsync(
            """
            struct S { prop Stored = 5 }
            (new S()).Stored
            """));

    /// <summary>
    /// And a member that does not exist is still reported missing rather than
    /// silently answering null through the new lookup.
    /// </summary>
    [Fact]
    public async Task A_missing_member_is_still_reported()
    {
        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => new ToshEngine(ToshRuntime.CreateDefault()).ExecuteToListAsync(
                """
                struct S { prop Stored = 5 }
                (new S()).Nope
                """));

        Assert.Contains("Nope", exception.Message);
    }
}
