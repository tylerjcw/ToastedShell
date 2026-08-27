using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A trait's declared member types are a contract — `TOAST-0020`.
///
/// A class could satisfy `render() -> string` with an implementation returning `int`, and
/// nothing reported it. Half of what a trait is for could not be relied on: a caller
/// holding a `Display` still could not assume `render()` gave back a string, which makes
/// the trait a naming convention rather than a contract — and is exactly the assumption a
/// renderer or a compiler wants to make.
///
/// **Decided 2026-08-17: covariant returns, exact parameters, reported at class
/// definition.**
///
/// Checked in the *engine* rather than in `TypeChecker`, because the rule needs a subtype
/// relation and the checker holds annotation *names* while the engine holds declarations.
/// The trait-conformance block is the one place that already has both the trait and the
/// class in hand.
/// </summary>
public sealed class TraitMemberTypeTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? string.Empty : results[^1]?.ToString() ?? "null";
    }

    private static async Task<Exception> RefusedAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        return await Assert.ThrowsAnyAsync<Exception>(() => engine.ExecuteToListAsync(source));
    }

    // ── returns are covariant ──────────────────────────────────────────────────

    /// <summary>An unrelated return type is refused, at the declaration that wrote it.</summary>
    [Fact]
    public async Task An_unrelated_return_type_is_refused()
    {
        var error = await RefusedAsync(
            """
            trait TmtD { func render() -> string }
            class TmtBad uses TmtD { func render() -> int => 42 }
            """);

        Assert.Contains("render", error.Message, StringComparison.Ordinal);
        Assert.Contains("TmtD", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The exact type agrees, which is the ordinary case.</summary>
    [Fact]
    public async Task The_declared_type_agrees()
        => Assert.Equal("x", await RunAsync(
            """
            trait TmtD { func render() -> string }
            class TmtOk uses TmtD { func render() -> string => "x" }
            (new TmtOk()).render()
            """));

    /// <summary>
    /// **A derived type agrees** — narrowing a result never surprises a caller holding the
    /// trait, and a `make() -> Base` trait is the shape that wants it.
    /// </summary>
    [Fact]
    public async Task A_derived_return_type_agrees()
        => Assert.Equal("leaf", await RunAsync(
            """
            class TmtBase { prop K: string = "base" }
            class TmtLeaf extends TmtBase { prop K: string = "leaf" }
            trait TmtFactory { func make() -> TmtBase }
            class TmtMaker uses TmtFactory { func make() -> TmtLeaf => new TmtLeaf() }
            (new TmtMaker()).make().K
            """));

    /// <summary>
    /// An alias and its CLR spelling name the same type, so they agree. Comparing the
    /// written names alone would have refused this.
    /// </summary>
    [Fact]
    public async Task An_alias_agrees_with_its_clr_name()
        => Assert.Equal("1", await RunAsync(
            """
            trait TmtN { func n() -> int }
            class TmtC uses TmtN { func n() -> Int32 => 1 }
            (new TmtC()).n()
            """));

    // ── parameters are exact ───────────────────────────────────────────────────

    /// <summary>
    /// A parameter must name the same type. Contravariance would be sound and is
    /// deliberately not offered: it is rarely wanted, frequently misread, and half a
    /// variance rule is worse than a simple one.
    /// </summary>
    [Fact]
    public async Task A_mismatched_parameter_is_refused()
    {
        var error = await RefusedAsync(
            """
            trait TmtP { func take(v: string) }
            class TmtQ uses TmtP { func take(v: int) -> string => "x" }
            """);

        Assert.Contains("take", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A matching parameter agrees.</summary>
    [Fact]
    public async Task A_matching_parameter_agrees()
        => Assert.Equal("ok", await RunAsync(
            """
            trait TmtP { func take(v: string) }
            class TmtR uses TmtP { func take(v: string) -> string => "ok" }
            (new TmtR()).take("a")
            """));

    // ── silence means agreement ────────────────────────────────────────────────

    /// <summary>
    /// An undeclared type on either side agrees with anything: a trait that says nothing
    /// constrains nothing, and a class that says nothing has not contradicted the trait —
    /// it has only declined to repeat it.
    /// </summary>
    [Theory]
    [InlineData("trait TmtS { func r() -> string }\nclass TmtT uses TmtS { func r() => 42 }\n(new TmtT()).r()", "42")]
    [InlineData("trait TmtS { func r() }\nclass TmtT uses TmtS { func r() -> int => 42 }\n(new TmtT()).r()", "42")]
    public async Task An_undeclared_type_agrees_with_anything(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// A trait's own default body is unaffected — there is no class implementation to
    /// disagree with it.
    /// </summary>
    [Fact]
    public async Task A_trait_default_is_unaffected()
        => Assert.Equal("default", await RunAsync(
            """
            trait TmtU { func r() -> string => "default" }
            class TmtV uses TmtU { }
            (new TmtV()).r()
            """));

    // ── properties are invariant ───────────────────────────────────────────────

    /// <summary>
    /// A property's type must match **exactly**, unlike a return.
    /// </summary>
    /// <remarks>
    /// Decided 2026-08-17. A property is written as well as read, so narrowing it is
    /// unsound in a way a return is not — see the covariance test below for the shape that
    /// would break.
    /// </remarks>
    [Fact]
    public async Task A_mismatched_property_type_is_refused()
    {
        var error = await RefusedAsync(
            """
            trait TmtNamed { prop Name: string }
            class TmtBadProp uses TmtNamed { prop Name: int = 1 }
            """);

        Assert.Contains("Name", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A matching property agrees.</summary>
    [Fact]
    public async Task A_matching_property_agrees()
        => Assert.Equal("p", await RunAsync(
            """
            trait TmtNamed { prop Name: string }
            class TmtGoodProp uses TmtNamed { prop Name: string = "p" }
            (new TmtGoodProp()).Name
            """));

    /// <summary>
    /// **A narrowed property is refused even though the narrowing is a real subtype.** This
    /// is the case that separates a property from a return: code holding the trait could
    /// assign a `TmtPBase` into what the class declared as `TmtPLeaf`, and the class's own
    /// annotation would try to coerce it and fail — at the assignment, nowhere near the
    /// declaration that permitted it.
    /// </summary>
    [Fact]
    public async Task A_narrowed_property_is_refused_although_the_same_narrowing_is_allowed_for_a_return()
    {
        await RefusedAsync(
            """
            class TmtPBase { prop K: string = "b" }
            class TmtPLeaf extends TmtPBase { }
            trait TmtShaped { prop Shape: TmtPBase }
            class TmtNarrow uses TmtShaped { prop Shape: TmtPLeaf = new TmtPLeaf() }
            """);

        // The same narrowing, in return position, is accepted — which is the whole point of
        // the distinction rather than an inconsistency.
        Assert.Equal("b", await RunAsync(
            """
            class TmtPBase { prop K: string = "b" }
            class TmtPLeaf extends TmtPBase { }
            trait TmtFact { func make() -> TmtPBase }
            class TmtWide uses TmtFact { func make() -> TmtPLeaf => new TmtPLeaf() }
            (new TmtWide()).make().K
            """));
    }

    // ── interfaces get the same rule ───────────────────────────────────────────

    /// <summary>
    /// An interface is checked exactly as a trait is. They had the identical gap and sit in
    /// neighbouring blocks; two neighbouring constructs behaving differently would need a
    /// stated reason, and there is none.
    /// </summary>
    [Fact]
    public async Task An_interface_return_mismatch_is_refused()
    {
        var error = await RefusedAsync(
            """
            interface TmtIface { func f() -> string }
            class TmtIBad implements TmtIface { func f() -> int => 42 }
            """);

        // The contract and member, not the word "interface" — that lives in the Help text,
        // which is not part of the message.
        Assert.Contains("TmtIface.f", error.Message, StringComparison.Ordinal);
    }

    /// <summary>An interface return may narrow, like a trait's.</summary>
    [Fact]
    public async Task An_interface_return_may_narrow()
        => Assert.Equal("leaf", await RunAsync(
            """
            class TmtIBase { prop K: string = "base" }
            class TmtILeaf extends TmtIBase { prop K: string = "leaf" }
            interface TmtIFactory { func make() -> TmtIBase }
            class TmtIMaker implements TmtIFactory { func make() -> TmtILeaf => new TmtILeaf() }
            (new TmtIMaker()).make().K
            """));

    /// <summary>And an interface parameter must match exactly.</summary>
    [Fact]
    public async Task An_interface_parameter_mismatch_is_refused()
        => await RefusedAsync(
            """
            interface TmtIP { func take(v: string) -> string }
            class TmtIQ implements TmtIP { func take(v: int) -> string => "x" }
            """);

    /// <summary>A conforming interface implementation is unaffected.</summary>
    [Fact]
    public async Task A_conforming_interface_implementation_agrees()
        => Assert.Equal("x", await RunAsync(
            """
            interface TmtIOk { func f(v: string) -> string }
            class TmtIGood implements TmtIOk { func f(v: string) -> string => "x" }
            (new TmtIGood()).f("a")
            """));
}
