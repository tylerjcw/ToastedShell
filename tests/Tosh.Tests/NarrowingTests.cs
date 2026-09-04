using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Flow-sensitive typing, first slice — <c>TOAST-0084</c>.
/// </summary>
/// <remarks>
/// <para>
/// The item describes a control-flow analysis. What it did not say is that the checker was not
/// looking at the place its own examples put the code: a returned expression was checked for
/// its *type* against the declaration and never walked, so nothing inside it was examined.
/// `writeline $s.Nonexistent` reported a missing member; `return $s.Nonexistent` reported
/// nothing.
/// </para>
/// <para>
/// That mattered more than a missed diagnostic. Narrowing built on top of it would have been
/// unobservable — `if ($n is Leaf) { return $n.Value }` and a bare `return $n.Value` were both
/// silent, so a test for narrowing would have passed before the feature existed.
/// </para>
/// </remarks>
public sealed class NarrowingTests
{
    private const string Hierarchy = """
        class Node { prop Kind: string = "node" }
        class Leaf extends Node { prop Value: int = 7 }
        """;

    private static IReadOnlyList<ToshDiagnostic> Check(string source)
    {
        var runtime = ToshRuntime.CreateDefault();
        var parsed = Tosh.Language.Parsing.ToshParser.Parse(source, "<test>");
        var unit = Tosh.Language.Binding.Lowerer.Lower(parsed, runtime.Language.Commands);
        return Tosh.Language.Binding.TypeChecker.Check(unit);
    }

    /// <summary>
    /// Checks with the core prelude's declarations in scope, the way the engine does.
    /// </summary>
    private static IReadOnlyList<ToshDiagnostic> CheckWithPrelude(string source)
    {
        var runtime = ToshRuntime.CreateDefault();
        var parsed = Tosh.Language.Parsing.ToshParser.Parse(source, "<test>");
        var prelude = Tosh.Language.Parsing.ToshParser.Parse(
            Tosh.Language.CorePrelude.Source, "<core-prelude>");

        var unit = Tosh.Language.Binding.Lowerer.Lower(
            parsed, runtime.Language.Commands, ambientTypes: prelude.Statement);

        return Tosh.Language.Binding.TypeChecker.Check(unit);
    }

    private static bool PreludeFlagsMissingMember(string source) =>
        CheckWithPrelude(source).Any(d => d.Code == "tosh.type.member_not_found");

    private static bool FlagsMissingMember(string source) =>
        Check(source).Any(d => d.Code == "tosh.type.member_not_found");

    /// <summary>
    /// A returned expression is walked, which is what makes any of the rest observable.
    /// </summary>
    [Fact]
    public void A_returned_expression_is_checked()
    {
        Assert.True(FlagsMissingMember("func f(s: string) -> int { return $s.Nonexistent }"));
    }

    /// <summary>
    /// It was already checked outside a return, which is what made the gap hard to see.
    /// </summary>
    [Fact]
    public void A_non_returned_expression_was_already_checked()
    {
        Assert.True(FlagsMissingMember("""
            func f(s: string) -> int {
                writeline $s.Nonexistent
                return 0
            }
            """));
    }

    /// <summary>
    /// A parameter's declared type reaches the member checker — it always did, and only the
    /// return gap made it look otherwise.
    /// </summary>
    [Fact]
    public void An_un_narrowed_parameter_reports_a_derived_member()
    {
        Assert.True(FlagsMissingMember(Hierarchy + "\nfunc f(n: Node) -> int { return $n.Value }"));
    }

    /// <summary>
    /// `if ($x is T)` narrows its then-branch. This is the specification's own example, and it
    /// failed: `§Type Narrowing` says "Both `if` and a `match` arm narrow, and they narrow
    /// identically", while only the match arm did.
    /// </summary>
    [Fact]
    public void An_if_condition_narrows_its_then_branch()
    {
        Assert.False(FlagsMissingMember(Hierarchy + """

            func describe(n: Node) -> string {
                if ($n is Leaf) { return $"leaf {$n.Value}" }
                return $n.Kind
            }
            """));
    }

    [Fact]
    public void A_match_arm_narrows_as_it_did_before()
    {
        Assert.False(FlagsMissingMember(Hierarchy + """

            func f(n: Node) -> int {
                return (match ($n) {
                    _ is Leaf => $n.Value
                    default => 0
                })
            }
            """));
    }

    /// <summary>
    /// The narrowing ends with the branch. Without this the feature would be a way to silence
    /// the checker rather than to inform it.
    /// </summary>
    [Fact]
    public void A_narrowing_does_not_outlive_its_branch()
    {
        Assert.True(FlagsMissingMember(Hierarchy + """

            func f(n: Node) -> int {
                if ($n is Leaf) { writeline $n.Value }
                return $n.Value
            }
            """));
    }

    /// <summary>
    /// A condition that is not `$variable is Type` narrows nothing, rather than guessing.
    /// </summary>
    [Theory]
    [InlineData("if ($n.Kind == \"leaf\") { return $n.Value }\n    return 0")]
    [InlineData("if (true) { return $n.Value }\n    return 0")]
    public void An_unrelated_condition_narrows_nothing(string body)
    {
        Assert.True(FlagsMissingMember(Hierarchy + $"\nfunc f(n: Node) -> int {{\n    {body}\n}}"));
    }

    // ── The else branch of a negative test (`TOAST-0084`) ─────────────────────

    /// <summary>
    /// <c>is-not T</c> and <c>not (x is T)</c> carry the positive fact into the <em>else</em>
    /// branch.
    /// </summary>
    /// <remarks>
    /// Only <c>is</c> narrowed, and only its then-branch, so the identical information written
    /// the other way round was discarded: <c>if ($n is-not Leaf) { … } else { HERE }</c> knows
    /// exactly as much as <c>if ($n is Leaf) { HERE }</c> does.
    ///
    /// Subtracting <c>T</c> from the then-branch of a *positive* test is still not done, because
    /// it needs a type the model cannot spell. That asymmetry is why the two spellings are not
    /// interchangeable, and why the negative one is worth recognising on its own.
    /// </remarks>
    [Fact]
    public void The_else_branch_of_is_not_is_narrowed()
    {
        Assert.False(FlagsMissingMember(Hierarchy + """

            func describe(n: Node) -> string {
                if ($n is-not Leaf) { return $n.Kind } else { return $"leaf {$n.Value}" }
            }
            """));
    }

    [Fact]
    public void The_else_branch_of_a_negated_test_is_narrowed()
    {
        Assert.False(FlagsMissingMember(Hierarchy + """

            func describe(n: Node) -> string {
                if (not ($n is Leaf)) { return $n.Kind } else { return $"leaf {$n.Value}" }
            }
            """));
    }

    [Fact]
    public void The_then_branch_of_is_not_is_not_narrowed()
    {
        // The control that keeps the fact on the correct branch. In the then-branch the value is
        // precisely *not* a Leaf, so nothing may be assumed — a checker that narrowed here would
        // be unsound rather than merely generous.
        Assert.True(FlagsMissingMember(Hierarchy + """

            func describe(n: Node) -> string {
                if ($n is-not Leaf) { return $"leaf {$n.Value}" } else { return $n.Kind }
            }
            """));
    }

    [Fact]
    public void A_double_negation_returns_the_fact_to_the_then_branch()
    {
        Assert.False(FlagsMissingMember(Hierarchy + """

            func describe(n: Node) -> string {
                if (not ($n is-not Leaf)) { return $"leaf {$n.Value}" } else { return $n.Kind }
            }
            """));
    }

    [Fact]
    public void The_else_branch_of_a_positive_test_is_still_not_narrowed()
    {
        // Unchanged, and deliberately so: subtracting Leaf from this branch needs a type the
        // model cannot yet write down.
        Assert.True(FlagsMissingMember(Hierarchy + """

            func describe(n: Node) -> string {
                if ($n is Leaf) { return $n.Kind } else { return $"leaf {$n.Value}" }
            }
            """));
    }

    // ── Variant payload bindings (`TOAST-0084`) ───────────────────────────────

    private const string Union = """
        class Payload { prop Real: int = 1 }
        union Box {
            Full(v: Payload)
            Empty
        }
        """;

    /// <summary>
    /// A variant pattern's payload binding takes the field type the union declared.
    /// </summary>
    /// <remarks>
    /// <c>Full(v) =&gt; $v.Nope</c> reported nothing: the binding carried no type, so a member
    /// that does not exist on the payload was never checked. This is the box that makes
    /// destructuring worth having — <c>Ok(value)</c> should give <c>value</c> a type rather than
    /// a dynamic object that happens to carry the right thing at run time.
    ///
    /// The union is taken from the matched value's type rather than from the variant name. A
    /// name would need an index and would be ambiguous once two unions share a variant — the
    /// mistake <c>TOAST-0108</c> had to undo in the exhaustiveness checker.
    /// </remarks>
    [Fact]
    public void A_positional_payload_binding_is_typed()
    {
        Assert.True(FlagsMissingMember(Union + """

            func f(b: Box) -> int {
                return (match ($b) {
                    Full(v) => $v.Nope
                    Empty() => 0
                })
            }
            """));
    }

    [Fact]
    public void A_real_member_on_that_binding_is_accepted()
    {
        Assert.False(FlagsMissingMember(Union + """

            func f(b: Box) -> int {
                return (match ($b) {
                    Full(v) => $v.Real
                    Empty() => 0
                })
            }
            """));
    }

    [Theory]
    [InlineData("Full { v } => $v.Nope")]
    [InlineData("Full { v: got } => $got.Nope")]
    public void A_named_payload_binding_is_typed(string arm)
    {
        // Both the shorthand and the renaming form, since they bind through different syntax.
        Assert.True(FlagsMissingMember(Union + $$"""

            func f(b: Box) -> int {
                return (match ($b) {
                    {{arm}}
                    Empty() => 0
                })
            }
            """));
    }

    [Fact]
    public void A_named_binding_accepts_a_real_member()
    {
        Assert.False(FlagsMissingMember(Union + """

            func f(b: Box) -> int {
                return (match ($b) {
                    Full { v } => $v.Real
                    Empty() => 0
                })
            }
            """));
    }

    [Fact]
    public void An_untyped_payload_field_narrows_nothing()
    {
        // A union that did not say what it holds cannot have anything claimed about it. The
        // binding stays dynamic and the member check stays quiet, which is the honest answer
        // rather than a guess.
        Assert.False(FlagsMissingMember("""
            union Loose {
                Full(v)
                Empty
            }

            func f(b: Loose) -> int {
                return (match ($b) {
                    Full(v) => $v.Nope
                    Empty() => 0
                })
            }
            """));
    }

    // ── A generic union substitutes its type arguments (`TOAST-0084`) ─────────

    private const string GenericUnion = """
        class Payload { prop Real: int = 1 }
        union MyOpt<T> {
            Some(value: T)
            Nothing()
        }
        """;

    /// <summary>
    /// A payload field declared as one of the union's own type parameters takes what the use
    /// site supplied.
    /// </summary>
    /// <remarks>
    /// Without this the field type is the text <c>T</c>, which names no type anywhere, so the
    /// binding stayed dynamic and nothing was checked. <c>MyOpt&lt;Payload&gt;</c> resolves to a
    /// generic instance wrapping the union, and that is where the arguments live.
    /// </remarks>
    [Fact]
    public void A_generic_payload_takes_the_supplied_argument()
    {
        Assert.True(FlagsMissingMember(GenericUnion + """

            func f(o: MyOpt<Payload>) -> int {
                return (match ($o) {
                    Some(v) => $v.Nope
                    Nothing() => 0
                })
            }
            """));
    }

    [Fact]
    public void A_real_member_of_the_supplied_argument_is_accepted()
    {
        Assert.False(FlagsMissingMember(GenericUnion + """

            func f(o: MyOpt<Payload>) -> int {
                return (match ($o) {
                    Some(v) => $v.Real
                    Nothing() => 0
                })
            }
            """));
    }

    [Fact]
    public void The_bare_payload_spelling_is_typed_too()
    {
        // `Some(T)` — the spelling the core prelude's `Option` uses — declares the payload's
        // type without naming the field, and must substitute the same way.
        Assert.True(FlagsMissingMember("""
            class Payload { prop Real: int = 1 }
            union Bare<T> {
                Some(T)
                Nothing()
            }

            func f(o: Bare<Payload>) -> int {
                return (match ($o) {
                    Some(v) => $v.Nope
                    Nothing() => 0
                })
            }
            """));
    }

    [Fact]
    public void An_uninstantiated_generic_union_narrows_nothing()
    {
        // Without type arguments there is nothing to substitute, and `T` names no type. Staying
        // quiet is the honest answer; claiming a type here would be inventing one.
        Assert.False(FlagsMissingMember(GenericUnion + """

            func f(o: MyOpt) -> int {
                return (match ($o) {
                    Some(v) => $v.Nope
                    Nothing() => 0
                })
            }
            """));
    }

    // ── The core prelude's unions (`TOAST-0084`) ──────────────────────────────

    private const string Payloads = """
        class Payload { prop Real: int = 1 }
        class Problem { prop Why: string = "x" }
        """;

    /// <summary>
    /// Destructuring an <c>Option</c> types its payload, which is the case this box was filed
    /// for.
    /// </summary>
    /// <remarks>
    /// <c>Lowerer.Lower</c> built its type registry from the parsed source alone, so the
    /// prelude's <c>Option</c> and <c>Result</c> were not user types there at all — a union
    /// declared in the same file narrowed and an <c>Option</c> did not. The prelude's parse is
    /// the cached one the engine already loads it from, so nothing new is parsed to supply it.
    /// </remarks>
    [Fact]
    public void An_option_payload_is_typed()
    {
        Assert.True(PreludeFlagsMissingMember(Payloads + """

            func f(o: Option<Payload>) -> int {
                return (match ($o) {
                    Some(v) => $v.Nope
                    None() => 0
                })
            }
            """));
    }

    [Fact]
    public void A_real_member_of_an_option_payload_is_accepted()
    {
        Assert.False(PreludeFlagsMissingMember(Payloads + """

            func f(o: Option<Payload>) -> int {
                return (match ($o) {
                    Some(v) => $v.Real
                    None() => 0
                })
            }
            """));
    }

    [Theory]
    [InlineData("Ok(v) => $v.Nope", "Err(e) => \"\"")]
    [InlineData("Ok(v) => \"\"", "Err(e) => $e.Nope")]
    public void Both_of_a_results_type_parameters_substitute(string okArm, string errArm)
    {
        // `Result<T, E>` binds two parameters, and each arm sees a different one.
        Assert.True(PreludeFlagsMissingMember(Payloads + $$"""

            func f(r: Result<Payload, Problem>) -> string {
                return (match ($r) {
                    {{okArm}}
                    {{errArm}}
                })
            }
            """));
    }

    [Fact]
    public void A_results_real_members_are_accepted()
    {
        Assert.False(PreludeFlagsMissingMember(Payloads + """

            func f(r: Result<Payload, Problem>) -> string {
                return (match ($r) {
                    Ok(v) => $"{$v.Real}"
                    Err(e) => $e.Why
                })
            }
            """));
    }

    [Fact]
    public void A_source_union_still_displaces_an_ambient_one()
    {
        // The engine warns when a declaration takes a core type's name but does not refuse it,
        // so the registry has to agree about which one wins: the source declaration.
        Assert.True(PreludeFlagsMissingMember("""
            class Local { prop Here: int = 1 }
            union Option<T> {
                Some(only: Local)
                None()
            }

            func f(o: Option<Local>) -> int {
                return (match ($o) {
                    Some(v) => $v.Nope
                    None() => 0
                })
            }
            """));
    }

    [Fact]
    public void Without_the_prelude_nothing_changes()
    {
        // The parameter is optional, and a caller that supplies nothing behaves as it did.
        Assert.False(FlagsMissingMember(Payloads + """

            func f(o: Option<Payload>) -> int {
                return (match ($o) {
                    Some(v) => $v.Nope
                    None() => 0
                })
            }
            """));
    }
}
