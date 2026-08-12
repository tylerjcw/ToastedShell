using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// The preview type checks stay quiet on code that runs — <c>TS-P2-84</c>.
/// </summary>
/// <remarks>
/// <para>
/// Found by the false-positive sweep built for <c>TS-P2-79</c>: 18 diagnostics across the
/// repository's 56 scripts, none of them from that item's new checks. They are invisible at the
/// CLI, which filters <c>Preview</c>-lifecycle diagnostics, but the language server shows them —
/// so an editor was reporting warnings against scripts that run correctly, which is the one
/// outcome a preview check must not produce.
/// </para>
/// <para>
/// Four causes, each the same shape: the checker modelling something the runtime does
/// differently. A dynamic record's members are decided at runtime; a piped list is *enumerated*,
/// so the command downstream sees elements rather than the list; a list literal types as the
/// non-generic <c>IList</c> and the runtime converts it to whatever array the annotation asks
/// for; and a bare word in command position is text whose meaning the parameter decides.
/// </para>
/// </remarks>
public sealed class PreviewCheckFalsePositiveTests : IClassFixture<ToshRuntimeFixture>
{
    private readonly ToshRuntime _runtime;

    public PreviewCheckFalsePositiveTests(ToshRuntimeFixture fixture) => _runtime = fixture.Runtime;

    private IReadOnlyList<ToshDiagnostic> Check(string source)
    {
        var engine = new ToshEngine(_runtime);
        var unit = Lowerer.Lower(engine.Parse(source, "<preview-test>"), _runtime.Commands);
        return TypeChecker.Check(unit);
    }

    private async Task<object?> EvalAsync(string source)
    {
        var engine = new ToshEngine(_runtime);
        return (await engine.ExecuteToListAsync(source)).LastOrDefault();
    }

    [Theory]
    // A dynamic record's members exist only at runtime; ExpandoObject is sealed, so it passed the
    // soundness guard and every member of `{| Name = "a" |}` was reported missing.
    [InlineData("var r = {| Name = \"a\", Tags = [1] |}\n$r.Name", "a")]
    [InlineData("var r = {| Body = \"b\" |}\n$r.Body", "b")]
    // A piped list is enumerated, so what the command declares about lists says nothing.
    [InlineData("[1,2,3] | each { $_ * 2 } | count", "3")]
    [InlineData("[\"a\",\"b\"] | where { $_ == \"a\" } | count", "1")]
    // A list literal types as the non-generic IList; `-> object[]` is a concrete CLR array, which
    // the structured rule missed.
    [InlineData("func f() -> object[] { return [1,2] }\nf | count", "2")]
    // A bare word in command position is text the annotation converts.
    [InlineData("func bigFiles(minSize: StorageSize) { return $minSize }\nbigFiles 512b", "512 B")]
    public async Task Code_that_runs_is_not_reported(string source, string expected)
    {
        // Both halves matter: it has to run, and it has to be quiet about running.
        Assert.Equal(expected, (await EvalAsync(source))?.ToString());
        Assert.Empty(Check(source).Where(d => d.Code.StartsWith("tosh.type.", StringComparison.Ordinal)));
    }

    // ── the checks must still catch what is genuinely wrong ────────────────────

    [Theory]
    [InlineData("func f(n: int) { return $n }\nf \"abc\"", "tosh.type.mismatch")]
    [InlineData("var x: int = \"42\"", "tosh.type.mismatch")]
    [InlineData("class C { prop X: int = 0 }\nvar c = new C()\n$c.Nope", "tosh.type.member_not_found")]
    public void A_genuine_mistake_is_still_reported(string source, string code)
    {
        Assert.Contains(Check(source), d => d.Code == code);
    }

    [Fact]
    public void A_quoted_string_is_still_a_string()
    {
        // The bareword exemption must not swallow a real one: `"abc"` was written as text on
        // purpose, and passing it where an `int` is declared is still a mistake.
        Assert.Contains(Check("func f(n: int) { return $n }\nf \"abc\""), d => d.Code == "tosh.type.mismatch");
    }
}
