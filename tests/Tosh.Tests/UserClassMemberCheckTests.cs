using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// The checker resolves members of a ToastScript class from its declaration — <c>TS-P2-79</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every member rule worked from <c>targetType.ClrType</c>, and a ToastScript class has none
/// until it executes, so each one bailed on its first line. The three positions <c>TS-P2-22</c>
/// left open — a method parameter, a constructor parameter, a property assignment — reported
/// nothing at all.
/// </para>
/// <para>
/// What was needed was already there: <c>Lowerer.BuildUserTypeRegistry</c> harvests every
/// declaration into a <c>UserClassType</c> carrying the *syntax* node, and a variable's symbol
/// already lifts that type onto its references. Only the reading was missing.
/// </para>
/// <para>
/// These are <c>Preview</c>-lifecycle diagnostics, which the CLI filters out — a probe through
/// the CLI shows nothing for a CLR type either, which is how an early measurement here was
/// misread. They are asserted through <c>TypeChecker.Check</c>, the surface the language server
/// consumes.
/// </para>
/// </remarks>
public sealed class UserClassMemberCheckTests : IClassFixture<ToshRuntimeFixture>
{
    private readonly ToshRuntime _runtime;

    public UserClassMemberCheckTests(ToshRuntimeFixture fixture) => _runtime = fixture.Runtime;

    private IReadOnlyList<ToshDiagnostic> Check(string source)
    {
        var engine = new ToshEngine(_runtime);
        var unit = Lowerer.Lower(engine.Parse(source, "<user-member-test>"), _runtime.Commands);
        return TypeChecker.Check(unit);
    }

    private bool Reports(string source, string code) => Check(source).Any(d => d.Code == code);

    // ── the three positions TS-P2-22 left open ─────────────────────────────────

    [Theory]
    [InlineData("class C { func m(x: int) { return 1 } }\nvar c = new C()\n$c.m(\"abc\")")]
    [InlineData("class C { C(x: int) { } }\nvar c = new C(\"abc\")")]
    [InlineData("class C { prop X: int = 0 }\nvar c = new C()\n$c.X = \"abc\"")]
    public void An_annotation_is_checked_in_every_member_position(string source)
    {
        Assert.True(Reports(source, "tosh.type.mismatch"), source);
    }

    [Theory]
    [InlineData("class C { func m(x: int) { return 1 } }\nvar c = new C()\n$c.m(5)")]
    [InlineData("class C { C(x: int) { } }\nvar c = new C(5)")]
    [InlineData("class C { prop X: int = 0 }\nvar c = new C()\n$c.X = 5")]
    // No annotation commits to a type, so nothing can disagree with it.
    [InlineData("class C { func m(x) { return 1 } }\nvar c = new C()\n$c.m(\"abc\")")]
    [InlineData("class C { prop X = 0 }\nvar c = new C()\n$c.X = \"abc\"")]
    public void A_matching_or_unannotated_position_is_quiet(string source)
    {
        Assert.False(Reports(source, "tosh.type.mismatch"), source);
    }

    [Fact]
    public void A_struct_is_checked_the_same_way()
    {
        Assert.True(Reports(
            "struct S { func m(x: int) { return 1 } }\nvar s = new S()\n$s.m(\"abc\")",
            "tosh.type.mismatch"));
    }

    // ── member existence, which the same lookup gives for free ─────────────────

    [Theory]
    [InlineData("class C { func m(x: int) { return 1 } }\nvar c = new C()\n$c.nope()")]
    [InlineData("class C { prop X: int = 0 }\nvar c = new C()\n$c.Nope")]
    // A struct field is known, so a name that is not one is still reported.
    [InlineData("struct R(a: int)\nvar r = new R(1)\n$r.zzz")]
    public void An_undeclared_member_is_reported(string source)
    {
        Assert.True(Reports(source, "tosh.type.member_not_found"), source);
    }

    [Theory]
    // A base class, a trait, an interface or a partial half can carry members this declaration
    // does not list, and none are reachable from the checker — so absence proves nothing and
    // must stay silent. This is the boundary that keeps the check free of false positives.
    [InlineData("class B { }\nclass C extends B { }\nvar c = new C()\n$c.Inherited")]
    [InlineData("partial class C { }\nvar c = new C()\n$c.Elsewhere")]
    // `struct R(a: int)` puts its parameters in `Fields`, not `Members`, and they are ordinary
    // readable members of the value. Reading only `Members` reported them missing against a
    // struct that answers them — a false positive the first cut of this check shipped, missed
    // because no swept script used that form with member access.
    [InlineData("struct R(a: int, b: int)\nvar r = new R(1, 2)\n$r.a")]
    [InlineData("struct R(a: int, b: int)\nvar r = new R(1, 2)\n$r.b")]
    public void An_incomplete_declaration_reports_no_absence(string source)
    {
        Assert.False(Reports(source, "tosh.type.member_not_found"), source);
    }

    // ── overloads and shapes that must not be guessed at ───────────────────────

    [Fact]
    public void One_accepting_overload_makes_the_call_good()
    {
        Assert.False(Reports(
            "class C { func m(x: int) { return 1 }\nfunc m(x: string) { return 2 } }\n" +
            "var c = new C()\n$c.m(\"abc\")",
            "tosh.type.mismatch"));
    }

    [Theory]
    // Optional and rest parameters, named and splatted arguments: each makes the mapping from
    // argument to parameter something this does not model, so each is left alone.
    [InlineData("class C { func m(x: int = 1) { return 1 } }\nvar c = new C()\n$c.m(\"abc\")")]
    [InlineData("class C { func m(xs...) { return 1 } }\nvar c = new C()\n$c.m(\"abc\")")]
    [InlineData("class C { func m(x: int) { return 1 } }\nvar c = new C()\n$c.m(x = \"abc\")")]
    public void A_shape_that_cannot_be_mapped_is_left_alone(string source)
    {
        Assert.False(Reports(source, "tosh.type.mismatch"), source);
    }

    [Fact]
    public void A_compound_assignment_is_not_checked_as_a_plain_one()
    {
        // `+=` combines the existing value with the operand, so the assigned type is not the
        // operand's type and reporting on it would be wrong.
        Assert.False(Reports(
            "class C { prop X: int = 0 }\nvar c = new C()\n$c.X += \"abc\"",
            "tosh.type.mismatch"));
    }

    // ── the check must not fire on working code ────────────────────────────────

    [Fact]
    public void The_repositorys_own_class_using_scripts_are_clean()
    {
        // A new static check over a dynamic language earns its place by staying quiet on code
        // that runs. Scoped to the user-class diagnostics this item adds: the repository's
        // scripts do trip other, pre-existing preview checks, which are not this item's to fix.
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var scripts = Directory
            .EnumerateFiles(Path.Combine(root, "examples"), "*.tosh", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("class ", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(scripts);

        var hits = new List<string>();

        foreach (var path in scripts)
        {
            var engine = new ToshEngine(_runtime);
            var parse = engine.Parse(File.ReadAllText(path), path);
            if (parse.Diagnostics.Count > 0) continue;

            var unit = Lowerer.Lower(parse, _runtime.Commands);

            hits.AddRange(TypeChecker.Check(unit)
                .Where(d => d.Title.Contains("Constructor of '", StringComparison.Ordinal) ||
                            d.Title.Contains("' on '", StringComparison.Ordinal) && d.Code == "tosh.type.mismatch")
                .Select(d => $"{Path.GetFileName(path)}: {d.Title}"));
        }

        Assert.True(hits.Count == 0, "user-class checks fired on working code:\n  " + string.Join("\n  ", hits));
    }
}
