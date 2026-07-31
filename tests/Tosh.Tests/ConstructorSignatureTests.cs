using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A class is never ambiguous with itself — <c>TS-P1-18</c>.
/// </summary>
/// <remarks>
/// <para>
/// `class C(x: int) { C(y: int) { … } }` registered the explicit constructor *and* the
/// synthesized primary one, so every instantiation failed with "Multiple constructor overloads
/// matched class 'C' with 1 argument(s): C(y: int); C(x: int)" — a class reported as ambiguous
/// with itself, at the point of use, naming neither declaration as the thing to fix.
/// </para>
/// <para>
/// The rule compares **type annotations positionally, not arity**, and that distinction is the
/// whole of the design. Same-arity constructors are legal and work today — `G(n: int)` beside
/// `G(s: string)`, and a primary `H(x: int)` beside an explicit `H(s: string)`, both resolve
/// correctly. A blanket "same arity is an error" rule would have broken both, which is why the
/// working cases are asserted here first.
/// </para>
/// <para>
/// Rejecting only *identical* signatures also means this cannot break a working program: a class
/// it refuses could never have been instantiated at that signature anyway. The error simply
/// arrives at the declaration, where the mistake is, instead of at every call site.
/// </para>
/// </remarks>
public sealed class ConstructorSignatureTests
{
    private static async Task<object?> EvaluateAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? null : results[^1];
    }

    private static async Task<string> DeclarationErrorAsync(string source)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await EvaluateAsync(source));
        return error.Message;
    }

    // ── What must keep working ─────────────────────────────────────────────────

    [Fact]
    public async Task Same_arity_constructors_with_different_types_still_overload()
    {
        Assert.Equal("int:7/str:hi", await EvaluateAsync(
            """
            class G {
                prop X: string = ""
                G(n: int) { $this.X = $"int:{$n}" }
                G(s: string) { $this.X = $"str:{$s}" }
            }
            var a = (new G(7))
            var b = (new G("hi"))
            $"{$a.X}/{$b.X}"
            """));
    }

    [Fact]
    public async Task A_primary_and_an_explicit_constructor_of_different_types_coexist()
    {
        // The shape the fix most easily breaks: the primary is still synthesized as an overload,
        // it simply no longer collides.
        Assert.Equal("primary/str:hi", await EvaluateAsync(
            """
            class H(x: int) {
                prop X: string = "primary"
                H(s: string) { $this.X = $"str:{$s}" }
            }
            var a = (new H(7))
            var b = (new H("hi"))
            $"{$a.X}/{$b.X}"
            """));
    }

    [Fact]
    public async Task A_primary_and_an_explicit_constructor_of_different_arity_coexist()
    {
        Assert.Equal(5, await EvaluateAsync(
            """
            class D(x: int) {
                prop X: int = x
                D(a: int, b: int) { $this.X = ($a + $b) }
            }
            (new D(5)).X
            """));
    }

    [Fact]
    public async Task An_explicit_constructor_alone_is_untouched()
    {
        Assert.Equal(42, await EvaluateAsync(
            """
            class N {
                prop X: int = 0
                N() { $this.X = 42 }
            }
            (new N()).X
            """));
    }

    // ── What is now rejected, at the declaration ───────────────────────────────

    [Fact]
    public async Task An_explicit_constructor_matching_the_primary_is_a_declaration_error()
    {
        var message = await DeclarationErrorAsync(
            """
            class C(x: int) {
                prop X: int = 0
                C(y: int) { $this.X = ($y * 2) }
            }
            """);

        Assert.Contains("same signature as its primary constructor", message, StringComparison.Ordinal);
        // The old failure named no declaration at all. This one has to name the class.
        Assert.Contains("'C'", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_error_arrives_without_any_instantiation()
    {
        // The point of moving it to declaration time: the class above is never constructed here,
        // and it still fails. Previously this program ran clean and only broke at `new C(…)`.
        await DeclarationErrorAsync("class C(x: int) {\n    C(y: int) { }\n}");
    }

    [Theory]
    // Two explicit constructors that collide are the same defect and get the same treatment,
    // reported as a duplicate pair rather than as a primary-constructor clash.
    [InlineData("class F {\n    F(y: int) { }\n    F(z: int) { }\n}")]
    // Unannotated parameters are their own signature and collide with each other.
    [InlineData("class J {\n    J(a) { }\n    J(b) { }\n}")]
    public async Task Two_explicit_constructors_with_one_signature_are_rejected(string source)
    {
        Assert.Contains(
            "two constructors with the same signature",
            await DeclarationErrorAsync(source),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_collision_contributed_by_a_later_partial_is_caught()
    {
        // Validation runs after the merge, not before: neither part is wrong on its own, and
        // checking each in isolation would miss this entirely.
        Assert.Contains(
            "same signature as its primary constructor",
            await DeclarationErrorAsync(
                """
                partial class M(x: int) { }
                partial class M {
                    M(y: int) { }
                }
                """),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_typed_and_an_untyped_parameter_are_not_treated_as_a_collision()
    {
        // Deliberately *not* rejected, though `new K(1)` is ambiguous between them: the class is
        // still usable for arguments only one can accept, so refusing the declaration would break
        // a working program. The remaining ambiguity is between two constructors the author
        // actually wrote, which the resolver reports by name — it is not a class ambiguous with
        // itself, which is what TS-P1-18 is about.
        //
        // This assertion exists because the opposite was assumed while designing the rule, and
        // measuring it changed the design.
        var message = await DeclarationErrorAsync(
            """
            class K {
                prop X: string = ""
                K(n: int) { $this.X = "typed" }
                K(o) { $this.X = "untyped" }
            }
            (new K(1))
            """);

        Assert.DoesNotContain("duplicate_constructor", message, StringComparison.Ordinal);
        Assert.Contains("Multiple constructor overloads", message, StringComparison.Ordinal);
    }
}
