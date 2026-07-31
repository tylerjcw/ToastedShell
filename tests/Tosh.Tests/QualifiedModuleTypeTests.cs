using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A module-qualified type name works as an annotation — <c>TS-P1-34</c> — and a module that
/// shadows a CLR type still reaches it on a member miss — <c>TS-P1-35</c>.
/// </summary>
/// <remarks>
/// <para>
/// Reported as "I can not access `type`s defined inside of modules that I am requiring":
/// `var x: ToastLib.Math.IntPercent = 60` raised <c>annotation_unknown_type</c> while the
/// bare `IntPercent` worked. The report named refinement types, but the cause was general —
/// every lookup on the annotation path took a flat name, so no dotted name resolved at all,
/// classes and records included.
/// </para>
/// <para>
/// The two items are filed and tested together because the first *exposed* the second. The
/// reporter's file declares `coerce Math.Clamp(_, 0, 100)` inside `module Math`, and the
/// leaked unqualified name had resolved `Math` in the requiring scope, where it fell through
/// to `System.Math`. Resolving the qualified name evaluates the coercion with the module in
/// scope, where `Math` is the module — which has no `Clamp`. Fixing the annotation alone
/// would have moved the reporter from one error to another.
/// </para>
/// </remarks>
public sealed class QualifiedModuleTypeTests
{
    private const string Module =
        """
        module Outer {
            module Inner {
                export type SmallInt = int where (_ >= 0 and _ <= 10) coerce 10
                export type Port = int where (_ >= 1 and _ <= 65535)
                export class Widget { prop Name = "w" }
                export record Point(X: int, Y: int)
            }
        }
        """;

    private static async Task<object?> EvaluateAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? null : results[^1];
    }

    private static Task<object?> WithModuleAsync(string source) =>
        EvaluateAsync($"{Module}\n{source}");

    // ── TS-P1-34: a qualified name resolves as an annotation ───────────────────

    [Theory]
    // In range: accepted unchanged.
    [InlineData("var a: Outer.Inner.SmallInt = 5\n$a", 5)]
    // Out of range: the refinement's own coercion runs. Resolving the name is not enough —
    // the annotation has to be the *same* definition, carrying its predicate and coercion.
    [InlineData("var a: Outer.Inner.SmallInt = 99\n$a", 10)]
    [InlineData("var a: Outer.Inner.Port = 8080\n$a", 8080)]
    public async Task A_qualified_refinement_annotation_resolves_and_enforces(
        string source,
        int expected)
    {
        Assert.Equal(expected, await WithModuleAsync(source));
    }

    [Fact]
    public async Task A_qualified_refinement_still_rejects_a_violation()
    {
        // `Port` has no coercion, so a violation must fail rather than pass silently. Making
        // an unknown annotation *known* must not make it toothless.
        var error = await Assert.ThrowsAnyAsync<Exception>(
            async () => await WithModuleAsync("var c: Outer.Inner.Port = 0"));

        Assert.Contains("refinement", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    // Not refinement-specific: classes and records failed identically, which is why the fix
    // is in the shared lookups rather than in the refinement path the report pointed at.
    [InlineData("var w: Outer.Inner.Widget = (new Outer.Inner.Widget())\n$w.Name", "w")]
    [InlineData("var p: Outer.Inner.Point = (new Outer.Inner.Point(1, 2))\n$p.X", 1)]
    public async Task A_qualified_class_or_record_annotation_resolves(
        string source,
        object expected)
    {
        Assert.Equal(expected, await WithModuleAsync(source));
    }

    [Theory]
    // The walk must not turn every dotted name into a match. A wrong leaf, a wrong module,
    // and a plain CLR-looking name that does not exist all still have to be rejected.
    [InlineData("var e: Outer.Inner.Nope = 1")]
    [InlineData("var e: Outer.Missing.SmallInt = 1")]
    [InlineData("var e: Missing.Inner.SmallInt = 1")]
    [InlineData("var e: Not.A.Real.Type = 1")]
    public async Task An_unknown_qualified_annotation_is_still_rejected(string source)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(
            async () => await WithModuleAsync(source));

        Assert.Contains("unknown type annotation", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_qualified_annotation_works_across_a_required_file()
    {
        // The reported route. Declared `partial`, as the reporter's library is, because
        // partial merging is a separate declaration path (TS-P2-28) from a plain module.
        var path = Path.Combine(Path.GetTempPath(), $"tosh-qualified-{Guid.NewGuid():N}.tosh");
        await File.WriteAllTextAsync(
            path,
            """
            partial module Lib {
                partial module Nums {
                    export type Small = int where (_ >= 0 and _ <= 10) coerce 10
                }
            }
            """);

        try
        {
            var escaped = path.Replace("\\", "\\\\", StringComparison.Ordinal);
            Assert.Equal(10, await EvaluateAsync(
                $"require \"{escaped}\"\nvar v: Lib.Nums.Small = 99\n$v"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── TS-P1-36: exports are reached through the module name, never bare ──────

    [Theory]
    // Types, classes and commands alike: `export` publishes a member *of the module*, and the
    // module's name is how you reach it. Nothing lands in the requiring scope.
    [InlineData("var v: SmallInt = 5", "unknown type annotation")]
    [InlineData("var w = (new Widget())", "Widget")]
    public async Task An_export_does_not_leak_unqualified_from_a_plain_module(
        string source,
        string expected)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(
            async () => await WithModuleAsync(source));

        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_export_does_not_leak_unqualified_from_a_partial_module()
    {
        // `partial` splits a declaration across files; it must not also widen visibility.
        // Asserted separately because partial merging declares through a different path
        // (TS-P2-28) than a plain module, so the two could drift apart without this.
        //
        // This is the decision recorded for TS-P1-36 — exports are reached through the module
        // name or an alias, never bare. The item was filed as a *defect* on the belief that a
        // partial module leaked its exports; that turned out to be unreproducible, and the
        // rule was already in force. It is pinned here so the decision is enforced rather than
        // merely true today.
        var path = Path.Combine(Path.GetTempPath(), $"tosh-noleak-{Guid.NewGuid():N}.tosh");
        await File.WriteAllTextAsync(
            path,
            """
            partial module Lib {
                partial module Nums {
                    export type Small = int where (_ >= 0 and _ <= 10) coerce 10
                    export func hello() { return "hi" }
                }
            }
            """);

        try
        {
            var escaped = path.Replace("\\", "\\\\", StringComparison.Ordinal);

            var typeError = await Assert.ThrowsAnyAsync<Exception>(
                async () => await EvaluateAsync(
                    $"require \"{escaped}\"\nvar v: Small = 5"));
            Assert.Contains("unknown type annotation", typeError.Message, StringComparison.Ordinal);

            var commandError = await Assert.ThrowsAnyAsync<Exception>(
                async () => await EvaluateAsync($"require \"{escaped}\"\nhello()"));
            Assert.Contains("hello", commandError.Message, StringComparison.Ordinal);

            // And the qualified spellings still work, which is the other half of the rule —
            // "not bare" is only correct if the module name genuinely reaches them.
            Assert.Equal(10, await EvaluateAsync(
                $"require \"{escaped}\"\nvar v: Lib.Nums.Small = 99\n$v"));
            Assert.Equal("hi", await EvaluateAsync(
                $"require \"{escaped}\"\nLib.Nums.hello()"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── TS-P1-35: a shadowing module still reaches the CLR type ────────────────

    /// <summary>
    /// The specification's own example, verbatim — <c>§Modules That Shadow CLR Types</c>.
    /// Spec examples are not otherwise executed by anything, so keeping this identical to the
    /// printed listing is what stops the document from drifting into describing behaviour the
    /// language does not have, as the LSP hover table had (<c>TS-P2-33</c>).
    /// </summary>
    private const string ShadowingModule =
        """
        module Math {
            export func Clamp(a, b, c) { return "module-wins" }
            export func widest() { return Math.Max(3, 7) }
        }
        """;

    [Fact]
    public async Task A_module_export_wins_over_the_clr_type_it_shadows()
    {
        // The fallback is reached only on a miss, so a module that *does* export the name
        // keeps answering. Asserted first because it is what the fallback must not break.
        Assert.Equal(
            "module-wins",
            await EvaluateAsync($"{ShadowingModule}\nMath.Clamp(1, 2, 3)"));
    }

    [Fact]
    public async Task A_shadowed_clr_member_is_reachable_from_inside_the_module()
    {
        // `Math.Max` from inside `module Math`: the module has no `Max`, so the shadowed
        // `System.Math` answers. This is the reporter's `coerce Math.Clamp(…)` in miniature.
        Assert.Equal(7, await EvaluateAsync($"{ShadowingModule}\nMath.widest()"));
    }

    [Fact]
    public async Task A_miss_on_a_module_shadowing_nothing_still_fails()
    {
        // The fallback must not swallow real mistakes into a silent null.
        var error = await Assert.ThrowsAnyAsync<Exception>(
            async () => await WithModuleAsync("Outer.Inner.Nope(1)"));

        Assert.Contains("Nope", error.Message, StringComparison.Ordinal);
    }
}
