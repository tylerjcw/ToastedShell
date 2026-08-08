using Tosh.Language.Parsing;

namespace Tosh.Tests;

/// <summary>
/// A deferred block cannot yield — <c>TS-P2-58</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>func g() { defer { yield 9 }\n yield 1 }</c> produced only <c>1</c>. The deferred block ran
/// and its side effects landed, so the missing value looked like correct behaviour rather than a
/// loss, and the generator terminated cleanly with nothing to report it.
/// </para>
/// <para>
/// Decided and refused rather than delivered: a deferred block runs while the function unwinds,
/// after a consumer may have stopped pulling, so there is no stream for it to join and no ordering
/// that could be written down honestly. Refusing at parse time costs nothing at runtime.
/// </para>
/// </remarks>
public sealed class DeferYieldTests
{
    private static IReadOnlyList<SyntaxDiagnostic> Parse(string source) =>
        ToshParser.Parse(source).Diagnostics;

    [Fact]
    public void The_reported_case_is_refused()
    {
        var diagnostic = Assert.Single(Parse("func g() {\n    defer { yield 9 }\n    yield 1\n}"));

        Assert.Equal("tosh.parser.yield_in_defer", diagnostic.Code);
        Assert.Contains("nowhere to go", diagnostic.Label!, StringComparison.Ordinal);
    }

    [Theory]
    // Anywhere inside the deferred block, however deeply nested.
    [InlineData("func g() { defer { if (true) { yield 9 } } }")]
    [InlineData("func g() { defer { for i in 1..2 { yield 9 } } }")]
    [InlineData("func g() { defer { while (false) { yield 9 } } }")]
    [InlineData("func g() { defer { try { yield 9 } catch (e) { } } }")]
    [InlineData("func g() { defer { defer { yield 9 } } }")]
    public void A_nested_yield_is_refused_too(string source)
    {
        Assert.Contains(Parse(source), d => d.Code == "tosh.parser.yield_in_defer");
    }

    [Fact]
    public void A_function_declared_in_the_deferred_block_keeps_its_yields()
    {
        // The one distinction the walk has to make: that yield belongs to `inner`, which is a
        // generator in its own right and has a stream of its own.
        Assert.DoesNotContain(
            Parse("func g() {\n    defer { func inner() { yield 9 } }\n    yield 1\n}"),
            d => d.Code == "tosh.parser.yield_in_defer");
    }

    [Theory]
    // Nothing else changed: a defer without a yield, and a yield outside one.
    [InlineData("func g() {\n    defer { echo x }\n    yield 1\n}")]
    [InlineData("func g() {\n    if (true) { yield 5 }\n    yield 1\n}")]
    [InlineData("func g() {\n    for i in 1..3 { yield $i }\n}")]
    public void Everything_that_already_parsed_still_does(string source)
    {
        Assert.DoesNotContain(Parse(source), d => d.Code == "tosh.parser.yield_in_defer");
    }
}
