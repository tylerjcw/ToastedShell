using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// An explicit constructor can chain to its class's primary constructor — <c>TS-P1-37</c>.
/// </summary>
/// <remarks>
/// <para>
/// A class may be written either way, and both worked:
/// </para>
/// <code>
/// class C { prop X = 5   C(x) { $this.X = $x } }     // explicit
/// class C(x) { prop X = $x }                          // primary
/// </code>
/// <para>
/// The gap was *mixing* them. With a primary constructor and an explicit one of another arity, a
/// property initializer reading a primary parameter failed when construction came in through the
/// explicit constructor — "Variable 'x' was not found", reported at the initializer, several
/// lines from the constructor that caused it. Property initializers run with the *selected*
/// constructor's locals, and the primary parameters were simply not among them.
/// </para>
/// <para>
/// <c>$this(...)</c> is the chain, spelled to mirror the <c>$super(...)</c> that already
/// initializes a base class: same recognition, same "first statement only" rule, pointed at this
/// class's own primary constructor instead of its parent's.
/// </para>
/// </remarks>
public sealed class PrimaryConstructorChainTests
{
    private static async Task<object?> EvaluateAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? null : results[^1];
    }

    private static async Task<string> ErrorFor(string source)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await EvaluateAsync(source));
        return error.Message;
    }

    // ── Both single-constructor forms keep working ─────────────────────────────

    [Theory]
    [InlineData("class C { prop X = 5\n    C(x) { $this.X = $x } }\n(new C(9)).X", 9)]
    [InlineData("class C(x) { prop X = $x }\n(new C(9)).X", 9)]
    public async Task Either_constructor_form_alone_is_unaffected(string source, int expected)
    {
        Assert.Equal(expected, await EvaluateAsync(source));
    }

    // ── The chain ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_chain_binds_the_primary_parameters_for_initializers()
    {
        // The reported failure, now working: `$x` in the initializer resolves because the chain
        // bound it before the initializer loop ran.
        Assert.Equal(5, await EvaluateAsync(
            """
            class C(x) {
                prop X = $x
                C(a, b) { $this(($a + $b)) }
            }
            (new C(2, 3)).X
            """));
    }

    [Fact]
    public async Task The_constructor_body_still_runs_after_the_chain()
    {
        // A chain is an initializer, not a replacement for the body.
        Assert.Equal("12/chained", await EvaluateAsync(
            """
            class D(x) {
                prop X = $x
                prop Tag = "none"
                D(a, b) {
                    $this(($a * $b))
                    $this.Tag = "chained"
                }
            }
            var d = (new D(3, 4))
            $"{$d.X}/{$d.Tag}"
            """));
    }

    [Fact]
    public async Task The_explicit_parameters_are_visible_to_the_chain_arguments()
    {
        // `$this($a + $b)` is the point of the feature: the chain argument is computed from the
        // parameters the caller actually supplied.
        Assert.Equal(30, await EvaluateAsync(
            """
            class E(total) {
                prop Total = $total
                E(a, b) { $this(($a * $b)) }
            }
            (new E(5, 6)).Total
            """));
    }

    [Fact]
    public async Task Constructing_through_the_primary_is_unaffected_by_the_chain()
    {
        // The class still has a primary constructor, and using it directly must not change.
        Assert.Equal(9, await EvaluateAsync(
            """
            class F(x) {
                prop X = $x
                F(a, b) { $this(($a + $b)) }
            }
            (new F(9)).X
            """));
    }

    // ── Guards ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_chain_without_a_primary_constructor_is_rejected()
    {
        Assert.Contains(
            "has no primary constructor",
            await ErrorFor("class G { prop X = 0\n    G(a) { $this($a) } }\nnew G(1)"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_chain_must_be_the_first_statement()
    {
        // Same rule as `$super(...)`: anything before it could read a primary parameter that is
        // not bound yet, which is the failure this feature exists to remove.
        Assert.Contains(
            "must be the first executable statement",
            await ErrorFor(
                """
                class H(x) {
                    prop X = $x
                    H(a, b) {
                        echo hi
                        $this($a)
                    }
                }
                new H(1, 2)
                """),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_constructor_may_chain_only_once()
    {
        Assert.Contains(
            "more than once",
            await ErrorFor(
                """
                class I(x) {
                    prop X = $x
                    I(a, b) {
                        $this($a)
                        $this($b)
                    }
                }
                new I(1, 2)
                """),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chain_arguments_are_checked_against_the_primary_signature()
    {
        // A chain cannot smuggle in something the primary constructor would have rejected: the
        // same binder runs as for a direct `new`.
        Assert.Contains(
            "does not match the primary constructor",
            await ErrorFor(
                """
                class J(x, y) {
                    prop X = $x
                    J(a) { $this($a) }
                }
                new J(1)
                """),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chaining_does_not_excuse_an_ambiguous_signature()
    {
        // TS-P1-18 rejects a primary and an explicit constructor of identical signature, and
        // chaining does not change that: `new K(5)` would still have two candidates and no way
        // to choose. The two rules compose rather than fight.
        Assert.Contains(
            "same signature as its primary constructor",
            await ErrorFor("class K(x) { prop X = $x\n    K(a) { $this($a) } }"),
            StringComparison.Ordinal);
    }
}
