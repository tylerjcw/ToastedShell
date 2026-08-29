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
}
