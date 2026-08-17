using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Executable fixtures for the specification's own examples — Test Strategy §1.
/// </summary>
/// <remarks>
/// <para>
/// The strategy section records that four specification examples were failing as
/// written, and that extracting them into fixtures "would have caught all four
/// mechanically". This is that extraction for the examples the specification
/// annotates with an expected value.
/// </para>
/// <para>
/// Candidates were harvested from the <c>lstlisting</c> blocks by
/// <c>scratchpad/spec_probe.py</c>: 242 lines carry a trailing comment, but most
/// are prose ("Variable", "int (System.Int32)") rather than expected results, so
/// only the 24 whose comment is a *value* are checkable. They are curated here
/// rather than extracted at test time, because a generic extractor cannot tell a
/// documented value from a description and would fail on shapes that are not
/// expressions at all — <c>$x += 5</c> among them.
/// </para>
/// <para>
/// The discovery run found one genuine defect: the overload examples were
/// written <c>$"one:$a"</c>, which does not interpolate — ToastScript holes are
/// braced, so the function returned the literal text <c>one:$a</c>. Corrected in
/// the specification and pinned below.
/// </para>
/// </remarks>
public sealed class SpecConformanceTests
{
    private static async Task<string?> EvaluateAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 1 ? results[0]?.ToString() : string.Join(", ", results);
    }

    [Theory]
    // Regex and quoting — the two examples TS-P2-12 was filed for.
    [InlineData("\"a1\" =~ \"\\d\"", "True")]
    [InlineData("\"file.cs\" =~ \"\\.cs$\"", "True")]
    // Units.
    [InlineData("5`s == 5000`ms", "True")]
    // Trait constraints.
    [InlineData("42 is Numeric", "True")]
    [InlineData("42 is Comparable", "True")]
    [InlineData("\"hello\" is Numeric", "False")]
    [InlineData("42 is-not Numeric", "False")]
    // Constants — the section was rewritten for TS-P1-12, so its examples are pinned here to
    // keep the prose and the implementation from drifting apart again.
    [InlineData("const MaxRetries = 3\n$MaxRetries", "3")]
    [InlineData("const X = 5\nif (true) { const X = 6 }\n$X", "5")]
    [InlineData("const Config = {| retries = 3 |}\n$Config.retries = 5\n$Config.retries", "5")]
    // CLR interop.
    [InlineData("String.Join(\", \", [\"a\", \"b\"])", "a, b")]
    [InlineData("Math.Sqrt(16)", "4")]
    [InlineData("Path.GetExtension(\"./file.cs\")", ".cs")]
    public async Task Specification_examples_produce_their_documented_values(
        string expression,
        string expected)
    {
        Assert.Equal(expected, await EvaluateAsync(expression));
    }

    [Fact]
    public async Task Overload_examples_interpolate_their_arguments()
    {
        // The defect this corpus found. Written `$"one:$a"` the function returned
        // the literal `one:$a`, because a ToastScript hole is braced. Both the
        // one- and two-argument overloads were affected, in two places.
        Assert.Equal("one:1", await EvaluateAsync(
            """
            func pick(a: int) -> string => $"one:{$a}"
            pick 1
            """));

        Assert.Equal("two:1+2", await EvaluateAsync(
            """
            func pick(a: int, b: int) -> string => $"two:{$a}+{$b}"
            pick 1 2
            """));
    }

    [Fact]
    public async Task An_unbraced_hole_is_literal_text()
    {
        // Pinning the rule that made the specification example wrong, so the
        // correction cannot quietly regress into the old spelling.
        Assert.Equal("one:$a", await EvaluateAsync(
            """
            func pick(a: int) -> string => $"one:$a"
            pick 1
            """));
    }

    [Fact]
    public async Task Comprehension_examples_match_the_specification()
    {
        // Formatted rather than ToString'd: a CLR array's ToString is its type
        // name, which would have compared a type against a value.
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var squares = Assert.Single(await engine.ExecuteToListAsync("[$x * $x <| for x in 1..5]"));
        Assert.Equal("[1, 4, 9, 16, 25]", engine.Runtime.Formatter.Format(squares));

        var doubled = await engine.ExecuteToListAsync("[1, 2, 3] | map func(x) => ($x * 2)");
        Assert.Equal(["2", "4", "6"], doubled.Select(v => v?.ToString()));
    }
}
