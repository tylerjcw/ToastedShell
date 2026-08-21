using Tosh.Language;
using Tosh.Language.Parsing;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Explicit type arguments parse and bind in static-call position — <c>TS-P2-82</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>Array.Empty&lt;int&gt;()</c> reported <c>tosh.parser.missing_pipeline_separator</c> — a
/// message about pipelines for a generic call — because the static-call test required <c>(</c>
/// immediately after the name, so the <c>&lt;</c> ended the command. The instance path had
/// allowed a type-argument list since generics were wired; only this one had not.
/// </para>
/// <para>
/// The runtime half was missing too: <c>InvokeStaticMethodAsync</c> took no type arguments, so
/// there was nowhere to put them even once they parsed. <c>ConstructGenericCandidates</c>, which
/// the instance path already used, serves both now.
/// </para>
/// <para>
/// This completes the half of <c>TS-P2-36</c>'s intent that inference could not reach: a call
/// with nothing to infer from is written explicitly instead.
/// </para>
/// </remarks>
public sealed class ExplicitTypeArgumentTests : IClassFixture<ToshRuntimeFixture>
{
    private readonly ToshRuntime _runtime;

    public ExplicitTypeArgumentTests(ToshRuntimeFixture fixture) => _runtime = fixture.Runtime;

    private async Task<object?> EvalAsync(string script)
    {
        var engine = new ToshEngine(_runtime);
        return (await engine.ExecuteToListAsync(script)).LastOrDefault();
    }

    [Theory]
    [InlineData("echo ...(Array.Empty<int>()) | count", "0")]
    [InlineData("echo ...(Enumerable.Empty<string>()) | count", "0")]
    [InlineData("Task.FromResult<int>(7) | await", "7")]
    [InlineData("Tuple.Create<int, int>(1, 2) | get Item2", "2")]
    [InlineData("echo ...(System.Array.Empty<string>()) | count", "0")]
    public async Task A_static_call_takes_explicit_type_arguments(string script, string expected)
    {
        Assert.Equal(expected, (await EvalAsync(script))?.ToString());
    }

    [Fact]
    public async Task An_unresolvable_type_argument_is_named()
    {
        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            async () => await EvalAsync("Array.Empty<NoSuchTypeAnywhere>()"));

        Assert.Contains(exception.Diagnostics, d => d.Code == "tosh.runtime.unknown_type");
    }

    // ── what must not change ───────────────────────────────────────────────────

    [Theory]
    // A lone `<` is still a comparison: the list counts only when it closes and a `(` follows.
    [InlineData("var A = 3\n($A < 5)", "True")]
    [InlineData("var A = 3\nvar b = 5\n($A < $b)", "True")]
    [InlineData("Task.FromResult(7) | await", "7")]
    [InlineData("Math.Max(1, 2)", "2")]
    [InlineData("echo ...(Enumerable.Range(1, 3)) | count", "3")]
    public async Task Neighbouring_forms_are_unaffected(string script, string expected)
    {
        Assert.Equal(expected, (await EvalAsync(script))?.ToString());
    }

    [Fact]
    public void A_comparison_against_a_capitalised_name_still_parses_clean()
    {
        var result = ToshParser.Parse("var Alpha = 1\nvar beta = 2\n($Alpha < $beta)", "<probe>");

        Assert.Empty(result.Diagnostics);
    }
}
