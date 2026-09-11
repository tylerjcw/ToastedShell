using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Constraints other than `Numeric`, and a closed generic contract — <c>TOAST-0125</c>.
/// </summary>
/// <remarks>
/// <para>
/// `where T: SomeInterface` and `where T: SomeBaseClass` were parsed, recorded and never
/// consulted: the checker accepted conservatively whenever the argument was not a
/// ToastScript class it recognised, which is every CLR type. So `where T: Drawable` took an
/// `int`. Only the built-in `Numeric` was ever enforced.
/// </para>
/// <para>
/// A CLR type cannot implement a ToastScript interface or extend a ToastScript class, so a
/// bound CLR argument is a definite failure rather than something to be conservative about.
/// A genuinely unrecognised name still is: refusing what cannot be checked turns an
/// unenforced constraint into a wrong error.
/// </para>
/// </remarks>
public sealed class GenericConstraintEnforcementTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? string.Empty : results[^1]?.ToString() ?? "null";
    }

    private const string Interfaces =
        "interface HasGet { func Get() -> int }\n"
        + "class Getter() fulfills HasGet { func Get() -> int => 7 }\n"
        + "class Plain() { }\n"
        + "class IfaceCon<T>(x: T) where T: HasGet { prop X: T = $x }\n";

    private const string Classes =
        "class Shape(n) { prop N = $n }\n"
        + "class Dot(n) extends Shape($n) { }\n"
        + "class Other() { }\n"
        + "class BaseCon<T>(x: T) where T: Shape { prop X: T = $x }\n";

    [Fact]
    public async Task An_interface_constraint_accepts_a_conforming_class()
        => Assert.Equal("7", await RunAsync($"{Interfaces}(new IfaceCon<Getter>(new Getter())).X.Get()"));

    [Theory]
    [InlineData("new IfaceCon<Plain>(new Plain())")]
    [InlineData("new IfaceCon<int>(1)")]
    [InlineData("new IfaceCon<string>(\"a\")")]
    public async Task An_interface_constraint_refuses_everything_else(string construction)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync($"{Interfaces}{construction}"));
        Assert.Contains("to satisfy 'HasGet'", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    // A class satisfies its own name and anything extending it, as in C#.
    [InlineData("new BaseCon<Shape>(new Shape(1))", "1")]
    [InlineData("new BaseCon<Dot>(new Dot(2))", "2")]
    public async Task A_base_class_constraint_accepts_the_class_and_its_subclasses(
        string construction,
        string expected)
        => Assert.Equal(expected, await RunAsync($"{Classes}({construction}).X.N"));

    [Theory]
    [InlineData("new BaseCon<Other>(new Other())")]
    [InlineData("new BaseCon<int>(1)")]
    public async Task A_base_class_constraint_refuses_everything_else(string construction)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync($"{Classes}{construction}"));
        Assert.Contains("to satisfy 'Shape'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_message_does_not_invent_a_clr_name_for_a_script_type()
    {
        // A ToastScript class has no CLR type of its own, and saying `(CLR <unresolved>)`
        // about it reads as a second failure.
        var error = await Assert.ThrowsAnyAsync<Exception>(
            () => RunAsync($"{Interfaces}new IfaceCon<Plain>(new Plain())"));

        Assert.Contains("'Plain' does not", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("<unresolved>", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    // `Numeric` is unchanged, and an unconstrained parameter still takes anything.
    [InlineData("class N<T>(x: T) where T: Numeric { prop X: T = $x }\n(new N<int>(1)).X", "1")]
    [InlineData("class U<T>(x: T) { prop X: T = $x }\n(new U<int>(1)).X", "1")]
    public async Task The_built_in_constraint_and_the_unconstrained_case_are_unchanged(
        string source,
        string expected)
        => Assert.Equal(expected, await RunAsync(source));

    // ── a closed generic contract ────────────────────────────────────────────

    [Theory]
    // `interface Co<T> { func Get() -> T }` fulfilled as `Co<int>` declares `Get` returning
    // `int`. Comparing the class's `-> int` against the unsubstituted `T` reported a
    // mismatch on every correct implementation there is.
    [InlineData("class I() fulfills Co<int> { func Get() -> int => 1 }\n(new I()).Get()", "1")]
    [InlineData("class S() fulfills Co<string> { func Get() -> string => \"s\" }\n(new S()).Get()", "s")]
    [InlineData("class O<T>(v: T) fulfills Co<T> { prop V: T = $v\n func Get() -> T => $this.V }\n(new O<int>(4)).Get()", "4")]
    public async Task A_closed_generic_interface_is_implemented_without_complaint(
        string implementation,
        string expected)
    {
        var source = "interface Co<T> { func Get() -> T }\n" + implementation;
        Assert.Equal(expected, await RunAsync(source));
    }

    [Fact]
    public async Task Two_parameters_are_both_substituted()
    {
        var source =
            "interface Two<A, B> { func Map(a: A) -> B }\n"
            + "class M() fulfills Two<int, string> { func Map(a: int) -> string => \"m\" }\n"
            + "(new M()).Map(1)";

        Assert.Equal("m", await RunAsync(source));
    }

    [Fact]
    public async Task A_genuine_mismatch_is_still_caught_and_names_the_closed_type()
    {
        var source =
            "interface Co<T> { func Get() -> T }\n"
            + "class Wrong() fulfills Co<int> { func Get() -> string => \"s\" }\n"
            + "(new Wrong()).Get()";

        var error = await Assert.ThrowsAnyAsync<ToshDiagnosticException>(() => RunAsync(source));
        var diagnostic = Assert.Single(
            error.Diagnostics,
            d => d.Code == "tosh.runtime.contract_member_type_mismatch");

        // Names `int`, not `T` — the reader is told what the contract actually requires.
        Assert.Contains("declares int", $"{diagnostic.Label}", StringComparison.Ordinal);
    }
}
