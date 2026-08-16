using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// An interpolation hole can carry a format and an alignment: <c>$"{expr,8:F2}"</c>.
///
/// `TS-P3-06`. The clause was lexed into the hole's text, so `$"{$pi:F2}"` tried to
/// run a command named `$pi:F2`. Formatting output is most of what a shell does, so
/// the workaround — `(3.14159).ToString("F2")` — was in the way constantly.
///
/// The clause is handed to the value's own <see cref="IFormattable"/>, which is what
/// makes every .NET format string work — numeric, date, enum — without the engine
/// knowing any of them.
///
/// **The one real ambiguity is the ternary colon**, and C# has it too: in
/// `$"{$x > 0 ? "a" : "b"}"` the colon belongs to the conditional. It is told apart
/// by counting `?` at the same nesting level, so a colon with an open question mark
/// before it closes that conditional rather than starting a format clause. `??` is
/// null-coalescing and opens nothing.
/// </summary>
public class InterpolationFormatTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? string.Empty : results[^1]?.ToString() ?? "null";
    }

    /// <summary>The clause reaches the value's own formatter, whatever the type.</summary>
    [Theory]
    [InlineData("var n = 3.14159\n$\"{$n:F2}\"", "3.14")]
    [InlineData("var n = 3.14159\n$\"{$n:F0}\"", "3")]
    [InlineData("var i = 42\n$\"{$i:X}\"", "2A")]
    [InlineData("var i = 42\n$\"{$i:D5}\"", "00042")]
    [InlineData("var i = 1234567\n$\"{$i:N0}\"", "1,234,567")]
    public async Task A_format_clause_is_applied(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// Alignment pads to a field width: positive right-aligns, negative left-aligns,
    /// as .NET composite formatting has it.
    /// </summary>
    [Theory]
    [InlineData("var i = 42\n$\"[{$i,6}]\"", "[    42]")]
    [InlineData("var i = 42\n$\"[{$i,-6}]\"", "[42    ]")]
    [InlineData("var n = 3.14159\n$\"[{$n,10:F3}]\"", "[     3.142]")]
    // Never truncated: a value wider than its field keeps all of itself.
    [InlineData("var s = \"toolong\"\n$\"[{$s,3}]\"", "[toolong]")]
    public async Task An_alignment_clause_pads_the_field(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// The ambiguity that decides the whole design. Each of these contains a colon or
    /// comma that belongs to the *expression*, and reading it as a clause would
    /// change the program's meaning rather than merely its formatting.
    /// </summary>
    [Theory]
    // A ternary's colon.
    [InlineData("var x = 5\n$\"{$x > 0 ? \"yes\" : \"no\"}\"", "yes")]
    // Nested conditionals: the count has to be a count, not a flag.
    [InlineData("var x = 5\n$\"{$x > 0 ? ($x > 3 ? \"big\" : \"small\") : \"neg\"}\"", "big")]
    // A comma inside a call is not an alignment.
    [InlineData("func add(a: int, b: int) -> int => ($a + $b)\n$\"{add(1, 2)}\"", "3")]
    // `??` is not a conditional and opens nothing.
    [InlineData("$\"{null ?? \"fallback\"}\"", "fallback")]
    // A colon inside a string literal belongs to the string.
    [InlineData("$\"{\"a:b\"}\"", "a:b")]
    // An index whose key contains a colon.
    [InlineData("var d = {% \"a:b\" => 7 %}\n$\"{$d[\"a:b\"]}\"", "7")]
    public async Task A_separator_belonging_to_the_expression_is_left_alone(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// A parenthesised conditional *can* still take a format, which is how a reader
    /// asks for both — and is what C# requires for the same reason.
    /// </summary>
    [Fact]
    public async Task A_parenthesised_conditional_may_still_be_formatted()
        => Assert.Equal("1.5", await RunAsync("var x = 5\n$\"{($x > 0 ? 1.5 : 2.5):F1}\""));

    /// <summary>Holes without a clause are untouched.</summary>
    [Theory]
    [InlineData("var n = 3.14159\n$\"{$n}\"", "3.14159")]
    [InlineData("var s = \"hi\"\n$\"{$s} there\"", "hi there")]
    [InlineData("var a = 1\nvar b = 2\n$\"{$a}-{$b}\"", "1-2")]
    public async Task A_hole_without_a_clause_is_unchanged(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// A clause the value cannot honour leaves the value readable rather than failing.
    /// A shell that refused to print because a format did not apply would be worse
    /// than one that prints plainly.
    /// </summary>
    [Fact]
    public async Task An_inapplicable_format_falls_back_to_plain_text()
        => Assert.Equal("hi", await RunAsync("var s = \"hi\"\n$\"{$s:F2}\""));

    /// <summary>
    /// Dates carry their own formats, and are the case that shows the clause is not
    /// a numeric feature bolted on.
    /// </summary>
    [Fact]
    public async Task A_date_takes_its_own_format()
        => Assert.Equal("2026-08-16", await RunAsync("$\"{(date 2026-08-16):yyyy-MM-dd}\""));

    /// <summary>
    /// The hole is still re-evaluated per pass, and the clause with it — the caching
    /// from `TS-P2-121` caches the *program*, and a format clause must not turn that
    /// into a cached string.
    /// </summary>
    [Fact]
    public async Task A_formatted_hole_is_re_evaluated_each_time()
        => Assert.Equal("1.00|2.00|3.00", await RunAsync(
            """
            var parts = ""
            for n in [1, 2, 3] { $parts = ($parts + $"{$n:F2}|") }
            $parts.TrimEnd("|")
            """));
}
