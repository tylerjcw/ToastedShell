using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A <c>match</c> over a closed union must cover every variant — <c>TOAST-0054</c>.
/// </summary>
/// <remarks>
/// <para>
/// The value is not in catching the first mistake. It is in what happens when a variant is
/// added to a union: either every <c>match</c> that must be updated is named now, or they are
/// found later, on someone else's input. It is an error rather than a warning from the first
/// release that has it, because retrofitting the check onto a codebase that has accumulated
/// non-exhaustive matches is how other languages ended up with one nobody enforces.
/// </para>
/// <para>
/// The union is identified from the <em>arms</em>, not from the matched value's type: a variant
/// name belongs to exactly one union, so a single arm is enough. That is what lets the check
/// run in the binder instead of waiting for the type checker to learn about unions, and it
/// costs nothing in coverage — a match with no variant arm at all is not a union match.
/// </para>
/// </remarks>
public sealed class MatchExhaustivenessTests
{
    private const string Union = """
        union Expr {
            Lit(v: int)
            Add(l: int, r: int)
            Neg(v: int)
        }
        """;

    private static async Task<IReadOnlyList<object?>> RunAsync(string body)
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);
        using var strict = engine.PushBinderStrictness(BinderStrictness.Strict);
        return await engine.ExecuteToListAsync(Union + "\n" + body);
    }

    private static async Task<Exception> FailureAsync(string body) =>
        await Assert.ThrowsAnyAsync<Exception>(async () => await RunAsync(body));

    [Fact]
    public async Task An_uncovered_variant_is_named()
    {
        var error = await FailureAsync("""
            echo (match (Expr.Lit(1)) {
                Lit(v) => $v
                Add(l, r) => $l
            })
            """);

        Assert.Contains("Neg", error.Message);
        Assert.Contains("Expr", error.Message);
    }

    /// <summary>
    /// Every uncovered variant is named, not merely the fact of incompleteness.
    /// </summary>
    [Fact]
    public async Task Every_uncovered_variant_is_named()
    {
        var error = await FailureAsync("""
            echo (match (Expr.Lit(1)) {
                Lit(v) => $v
            })
            """);

        Assert.Contains("Add", error.Message);
        Assert.Contains("Neg", error.Message);
    }

    [Fact]
    public async Task A_complete_match_is_accepted()
    {
        var results = await RunAsync("""
            echo (match (Expr.Lit(1)) {
                Lit(v) => $v
                Add(l, r) => $l
                Neg(v) => $v
            })
            """);

        Assert.Equal("1", results[^1]?.ToString());
    }

    /// <summary>
    /// <c>default</c> is the documented opt-out.
    /// </summary>
    [Fact]
    public async Task A_default_arm_satisfies_the_check()
    {
        var results = await RunAsync("""
            echo (match (Expr.Lit(1)) {
                Lit(v) => $v
                default => 0
            })
            """);

        Assert.Equal("1", results[^1]?.ToString());
    }

    /// <summary>
    /// A guarded arm may not fire, so it cannot complete the match — and the help says why,
    /// because this is the one refusal that reads like a false positive until explained.
    /// </summary>
    [Fact]
    public async Task A_guarded_arm_does_not_cover_its_variant()
    {
        var error = await FailureAsync("""
            echo (match (Expr.Lit(1)) {
                Lit(v) => $v
                Add(l, r) if (($l > 0)) => $l
                Neg(v) => $v
            })
            """);

        Assert.Contains("Add", error.Message);
    }

    [Fact]
    public async Task An_or_pattern_covers_each_of_its_alternatives()
    {
        var results = await RunAsync("""
            echo (match (Expr.Lit(1)) {
                (Lit(v) | Neg(v)) => $v
                Add(l, r) => $l
            })
            """);

        Assert.Equal("1", results[^1]?.ToString());
    }

    [Fact]
    public async Task An_as_binding_still_covers_its_variant()
    {
        var results = await RunAsync("""
            echo (match (Expr.Lit(1)) {
                Lit(v) as whole => $v
                Add(l, r) => $l
                Neg(v) => $v
            })
            """);

        Assert.Equal("1", results[^1]?.ToString());
    }

    /// <summary>
    /// Matching a non-union value gains no diagnostics — the check has to be invisible to
    /// shell code, which is most of what this language runs.
    /// </summary>
    [Fact]
    public async Task A_match_over_a_non_union_value_is_unaffected()
    {
        var results = await RunAsync("""
            echo (match (2) {
                1 => "one"
                2 => "two"
            })
            """);

        Assert.Equal("two", results[^1]?.ToString());
    }

    /// <summary>
    /// An arm that is not a variant pattern means this is not a union-shaped match, and
    /// nothing is claimed about it.
    /// </summary>
    [Fact]
    public async Task A_match_mixing_a_literal_arm_is_left_alone()
    {
        var results = await RunAsync("""
            echo (match (Expr.Lit(1)) {
                Lit(v) => $v
                "x" => 0
            })
            """);

        Assert.Equal("1", results[^1]?.ToString());
    }

    /// <summary>
    /// A union this source cannot see says nothing — a variant from a <c>require</c>d file is
    /// not collected, and claiming a set we cannot read would refuse working programs.
    /// </summary>
    [Fact]
    public async Task A_match_over_an_unseen_union_is_left_alone()
    {
        var results = await RunAsync("""
            echo (match (Expr.Lit(1)) {
                Imported(v) => $v
                default => 0
            })
            """);

        Assert.Equal("0", results[^1]?.ToString());
    }

    /// <summary>
    /// The property the item was filed for: adding a variant reports every site that must be
    /// updated, one diagnostic each, rather than leaving them to be found on live input.
    /// </summary>
    [Fact]
    public async Task Adding_a_variant_reports_every_uncovered_site()
    {
        const string Grown = """
            union Expr {
                Lit(v: int)
                Add(l: int, r: int)
                Neg(v: int)
                Mul(l: int, r: int)
            }
            echo (match (Expr.Lit(1)) {
                Lit(v) => $v
                Add(l, r) => $l
                Neg(v) => $v
            })
            echo (match (Expr.Lit(2)) {
                Lit(v) => $v
                Add(l, r) => $r
                Neg(v) => $v
            })
            """;

        var parsed = Tosh.Language.Parsing.ToshParser.Parse(Grown, "<test>");
        var runtime = ToshRuntime.CreateDefault();
        var diagnostics = Binder.Bind(parsed, runtime.Language.Commands, isInteractive: false);

        var exhaustiveness = diagnostics
            .Where(d => d.Code == "tosh.bind.match_not_exhaustive")
            .ToList();

        Assert.Equal(2, exhaustiveness.Count);
        Assert.All(exhaustiveness, d => Assert.Contains("Mul", d.Title));
    }

    /// <summary>
    /// `arg` and `flag` declare script inputs, which the binder did not know about.
    /// </summary>
    /// <remarks>
    /// It cost nothing while the binder walked only command arguments — the reference in
    /// `examples/mandelbrot.tosh` sat in `var tF = $frames`, an expression stage the walk
    /// skipped. Widening that walk made it visible, and a declared variable was reported as
    /// undeclared. Pinned here because nothing else in the suite covers it.
    /// </remarks>
    [Fact]
    public async Task A_script_input_parameter_is_a_declared_variable()
    {
        var parsed = Tosh.Language.Parsing.ToshParser.Parse(
            "arg frames : int = 100\nvar total = $frames\necho $total\n", "<test>");
        var runtime = ToshRuntime.CreateDefault();
        var diagnostics = Binder.Bind(parsed, runtime.Language.Commands, isInteractive: false);

        Assert.DoesNotContain(diagnostics, d => d.Code == "tosh.bind.unknown_variable");
        await Task.CompletedTask;
    }

    // ── Variant names shared with an ambient union (`TOAST-0108`) ─────────────

    /// <summary>
    /// A source union whose variant names collide with the core prelude's is measured against
    /// itself, not against the prelude — <c>TOAST-0108</c>.
    /// </summary>
    /// <remarks>
    /// Before this, `union Maybe { Some(v) Nothing }` matched on `Some` alone reported *"This
    /// match over 'Option' does not cover None"* — a union the author never wrote and a variant
    /// that is not in theirs. The variant index seeded ambient unions first and then used
    /// `TryAdd`, so a source declaration could never displace one, while the comment above it
    /// said the opposite.
    /// </remarks>
    private static async Task<IReadOnlyList<object?>> RunRawAsync(string source)
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime.Language);
        using var strict = engine.PushBinderStrictness(BinderStrictness.Strict);
        return await engine.ExecuteToListAsync(source);
    }

    private const string Shadowing = """
        union Maybe {
            Some(v: int)
            Nothing
        }
        """;

    [Fact]
    public async Task A_source_union_shadowing_an_ambient_one_is_named_in_the_diagnostic()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await RunRawAsync(
            Shadowing + """

            echo (match (Maybe.Some(1)) {
                Some(v) => $v
            })
            """));

        Assert.Contains("Maybe", error.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Option", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_shadowing_union_that_is_fully_covered_is_accepted()
    {
        // This one previously passed for the wrong reason: `Some` resolved to `Option` and
        // `Nothing` to `Maybe`, the two disagreed, and the check gave up silently.
        var results = await RunRawAsync(Shadowing + """

            echo (match (Maybe.Some(1)) {
                Some(v) => $v
                Nothing => 0
            })
            """);

        Assert.Equal("1", results[^1]?.ToString());
    }

    [Fact]
    public async Task The_ambient_union_still_resolves_when_nothing_shadows_it()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await RunRawAsync("""
            echo (match (Option::Some(1)) {
                Some(v) => $v
            })
            """));

        Assert.Contains("Option", error.Message, StringComparison.Ordinal);
        Assert.Contains("None", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Arms_disambiguate_each_other_when_one_alone_could_not()
    {
        // `Some` belongs to both `Trio` and `Option`; `None` only to `Option`. The intersection
        // is `Option`, which these two arms cover completely.
        //
        // Resolving from the first arm alone would pick `Trio` — a source declaration outranks
        // an ambient one — and then demand `Middle` and `Last`, a false error on a match that is
        // exhaustive over the union it is actually matching. That is what intersecting prevents,
        // so this asserts the *absence* of a diagnostic.
        var results = await RunRawAsync("""
            union Trio {
                Some(v: int)
                Middle(v: int)
                Last(v: int)
            }

            echo (match (Option::Some(1)) {
                Some(v) => $v
                None => 0
            })
            """);

        Assert.Equal("1", results[^1]?.ToString());
    }
}
