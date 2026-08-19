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
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? string.Empty : results[^1]?.ToString() ?? "null";
    }

    private static async Task<Exception> RefusedAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
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
}
