using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A property annotation is checked against its initializer — <c>TS-P2-22</c>, first of
/// four positions.
/// </summary>
/// <remarks>
/// <para>
/// The class members were already walked and the initializer *pipeline* was already
/// checked; what was missing is that the annotation was never compared against it. So
/// <c>prop X: int = "42"</c> reported nothing while <c>var x: int = "42"</c> reported
/// <c>tosh.type.mismatch</c> — silence rather than disagreement, since the runtime
/// converts in both cases. A hole in static coverage, and the worst kind: an annotation
/// that reads like a constraint and enforces nothing.
/// </para>
/// <para>
/// <b>Bounded on purpose.</b> Annotations arrive here as *names*, and the resolver is
/// built without user types — as <c>Lowerer</c>'s own probe is — so a name it cannot
/// resolve comes back <c>Dynamic</c> and is skipped rather than guessed at. The item's
/// other three positions (method parameter, constructor parameter, property assignment)
/// need the checker to resolve members of a *ToastScript* class, which it cannot do:
/// <c>CheckMemberAccess</c> works from <c>targetType.ClrType</c>, and a ToastScript class
/// instance has none at check time. That is a feature rather than an increment, and is
/// filed as <c>TS-P2-79</c>.
/// </para>
/// </remarks>
public sealed class ClassMemberAnnotationTests : IClassFixture<ToshRuntimeFixture>
{
    private readonly ToshRuntime _runtime;

    public ClassMemberAnnotationTests(ToshRuntimeFixture fixture) => _runtime = fixture.Runtime;

    /// <summary>Runs the type-check pass, the way `MemberCheckSoundnessTests` does.</summary>
    private IReadOnlyList<ToshDiagnostic> Diagnose(string source)
    {
        var engine = new ToshEngine(_runtime.Language);
        var parse = engine.Parse(source, "<annotation-test>");
        var unit = Lowerer.Lower(parse, _runtime.Commands);
        return TypeChecker.Check(unit);
    }

    private bool ReportsMismatch(string source) =>
        Diagnose(source).Any(d => d.Code == "tosh.type.mismatch");

    [Theory]
    [InlineData("class C { prop X: int = \"42\" }")]
    [InlineData("struct S { prop X: int = \"42\" }")]
    [InlineData("class C { prop X: bool = 3 }")]
    public void A_property_annotation_is_checked_against_its_initializer(string source)
    {
        Assert.True(ReportsMismatch(source), source);
    }

    [Theory]
    [InlineData("class C { prop X: int = 42 }")]
    [InlineData("class C { prop X: string = \"42\" }")]
    [InlineData("class C { prop X = \"42\" }")]           // no annotation to check
    [InlineData("struct S { prop X: int = 1 }")]
    public void A_matching_or_absent_annotation_is_quiet(string source)
    {
        Assert.False(ReportsMismatch(source), source);
    }

    [Fact]
    public void An_unresolvable_annotation_is_skipped_rather_than_guessed_at()
    {
        // The resolver has no user types, so a class or CLR name it cannot see comes back
        // Dynamic. Skipping keeps the pass free of false positives — the alternative is
        // warning about every annotation naming a user-declared type.
        Assert.False(ReportsMismatch("class C { prop X: SomeTypeNobodyDeclared = \"42\" }"));
    }

    [Fact]
    public void The_positions_that_already_worked_still_do()
    {
        Assert.True(ReportsMismatch("var x: int = \"42\""));
        Assert.True(ReportsMismatch("func f(x: int) { return 1 }\nf \"abc\""));
    }

    [Fact]
    public void The_diagnostic_names_the_property_and_both_types()
    {
        var diagnostic = Assert.Single(
            Diagnose("class C { prop Count: int = \"42\" }")
            .Where(d => d.Code == "tosh.type.mismatch"));

        // The CLR display name, not the annotation's spelling — worded exactly as the
        // sibling `var x: int = "42"` diagnostic is, so the two read alike.
        Assert.Contains("Count", diagnostic.Title, StringComparison.Ordinal);
        Assert.Contains("String", diagnostic.Title, StringComparison.Ordinal);
        Assert.Contains("Int32", diagnostic.Title, StringComparison.Ordinal);
        Assert.Equal(ToshDiagnosticSeverity.Warning, diagnostic.Severity);
    }
}
