using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// What an interpolation hole *is* — `TOAST-0023`.
///
/// A hole holding a collection **variable** spread it: `$"{$xs}"` gave `1 2 3`, while
/// `$"{[1, 2, 3]}"` and `$"{($xs)}"` both rendered `[1, 2, 3]`. Three spellings of one
/// value, two answers, decided by whether a variable happened to hold something
/// enumerable — and the compiled backend rendered in all three, which is how the
/// differential corpus found it.
///
/// **Decided 2026-08-17: a hole is one value unless it contains a pipeline.** An expression
/// renders; a pipeline joins its results with a single space. The line is a `|` the reader
/// can see in the source, rather than a runtime property of the value.
/// </summary>
public sealed class InterpolationHoleValueTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? string.Empty : results[^1]?.ToString() ?? "null";
    }

    /// <summary>
    /// Every spelling of the same value now agrees.
    /// </summary>
    [Theory]
    [InlineData("var xs = [1, 2, 3]\n$\"{$xs}\"", "[1, 2, 3]")]
    [InlineData("var xs = [1, 2, 3]\n$\"{($xs)}\"", "[1, 2, 3]")]
    [InlineData("$\"{[1, 2, 3]}\"", "[1, 2, 3]")]
    public async Task A_hole_holding_a_collection_renders_it(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// Which keeps the elements distinguishable. Spreading lost that: `["a b", "c"]` and
    /// `["a", "b", "c"]` both interpolated to `a b c`.
    /// </summary>
    [Fact]
    public async Task Spreading_no_longer_loses_the_element_boundaries()
    {
        Assert.Equal(
            """["a b", "c"]""",
            await RunAsync("var xs = [\"a b\", \"c\"]\n$\"{$xs}\""));

        Assert.Equal(
            """["a", "b", "c"]""",
            await RunAsync("var xs = [\"a\", \"b\", \"c\"]\n$\"{$xs}\""));
    }

    /// <summary>
    /// A hole that really does contain a pipeline still joins its results, with a single
    /// space — the other half of the decision, and what keeps `$"{ls | get Name}"` useful.
    /// </summary>
    [Theory]
    [InlineData("$\"{[1, 2, 3] | where { $_ > 1 }}\"", "2 3")]
    [InlineData("$\"{[1, 2, 3] | where { $_ > 99 }}\"", "")]
    public async Task A_hole_containing_a_pipeline_still_joins(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// Values that never spread are unchanged — they were already one value to the
    /// pipeline, and this must not have made them two.
    /// </summary>
    [Theory]
    [InlineData("var s = \"hi\"\n$\"{$s}\"", "hi")]
    [InlineData("var d = {% \"a\" => 1 %}\n$\"{$d}\"", "{% \"a\" => 1 %}")]
    [InlineData("var r = {| N = 1 |}\n$\"{$r}\"", "{| N = 1 |}")]
    [InlineData("$\"{1 + 2}\"", "3")]
    public async Task Single_values_are_unchanged(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// A rest argument's list renders as a list. Recorded because it is the shape most
    /// likely to appear in a real script, and the most visible consequence of the decision.
    /// </summary>
    [Fact]
    public async Task A_rest_parameters_list_renders_as_a_list()
        => Assert.Equal("""["a", "b"]""", await RunAsync(
            """
            func hole(items...) -> string => $"{$items}"
            hole "a" "b"
            """));
}
