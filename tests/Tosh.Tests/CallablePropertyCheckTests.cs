using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Calling a callable held in a property — `TS-P2-118`.
///
/// `$c.Handler(7)` where `Handler` holds a function reference warned
/// `tosh.type.member_not_found` and then returned 17. The runtime was right and the checker
/// was wrong, which is the worst pairing: the code works, so the warning is noise, and
/// noise is what teaches people to stop reading warnings.
///
/// `TS-P2-93` taught the `$this.Handler(…)` path that a property can hold a callable.
/// External access through an instance was never covered, so one rule lived in one path.
///
/// Asserted through `TypeChecker.Check` rather than the CLI: these are `Preview`-lifecycle
/// diagnostics, which the CLI filters, and this is the surface the language server consumes.
/// </summary>
public sealed class CallablePropertyCheckTests : IClassFixture<ToshRuntimeFixture>
{
    private readonly ToshRuntime _runtime;

    public CallablePropertyCheckTests(ToshRuntimeFixture fixture) => _runtime = fixture.Runtime;

    private IReadOnlyList<ToshDiagnostic> Check(string source)
    {
        var engine = new ToshEngine(_runtime);
        var unit = Lowerer.Lower(engine.Parse(source, "<callable-property-test>"), _runtime.Commands);
        return TypeChecker.Check(unit);
    }

    private bool ReportsMissingMember(string source)
        => Check(source).Any(d => d.Code == "tosh.type.member_not_found");

    /// <summary>
    /// Both spellings of a callable in a property: a `&amp;` reference and a lambda.
    /// </summary>
    [Theory]
    [InlineData("func addTen(n) { return ($n + 10) }\nclass Cp { prop Handler = &addTen }\nvar c = new Cp()\n$c.Handler(7)")]
    [InlineData("class Cp { prop F = func(x) => ($x * 3) }\nvar c = new Cp()\n$c.F(4)")]
    public void Calling_a_callable_property_through_an_instance_does_not_warn(string source)
        => Assert.False(ReportsMissingMember(source), source);

    /// <summary>
    /// The `$this` form was already right — `TS-P2-93` — and must stay so.
    /// </summary>
    [Fact]
    public void The_this_form_is_unchanged()
        => Assert.False(ReportsMissingMember(
            """
            func addTen(n) { return ($n + 10) }
            class Cp {
                prop Handler = &addTen
                func Use() -> int => $this.Handler(5)
            }
            """));

    /// <summary>
    /// A genuinely absent member still warns. Without this the fix could have been "stop
    /// warning", which removes the check rather than correcting it.
    /// </summary>
    [Fact]
    public void A_genuinely_missing_member_still_warns()
        => Assert.True(ReportsMissingMember(
            "class Cp { prop N: int = 1 }\nvar c = new Cp()\n$c.Nope(7)"));

    /// <summary>
    /// And a real method with the wrong argument count is still reported, so suppressing
    /// the property case did not suppress arity checking with it.
    /// </summary>
    [Fact]
    public void A_real_method_is_still_checked()
        => Assert.NotEmpty(Check("class Cp { func m(x: int) { return 1 } }\nvar c = new Cp()\n$c.m(\"abc\")"));
}
