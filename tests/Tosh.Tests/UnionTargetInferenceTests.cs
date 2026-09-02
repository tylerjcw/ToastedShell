using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A generic union's type arguments, inferred from where the value is going — <c>TOAST-0096</c>.
/// </summary>
/// <remarks>
/// <para>
/// Inference read only the constructor's arguments, so a unit variant had nothing to infer from
/// and demanded an explicit list even where the annotation or the signature had already said
/// what it was. `Opt.None&lt;int&gt;()` worked; `var o: Opt&lt;int&gt; = Opt.None()` did not.
/// </para>
/// <para>
/// Taken before <c>TOAST-0083</c> deliberately: `None` is the most common value in the
/// optionality story those core types exist to tell, and shipping them first would have baked
/// the repetition into every example of the feature.
/// </para>
/// </remarks>
public sealed class UnionTargetInferenceTests
{
    private const string Prelude =
        """
        union UtiOpt<T> { Some(T) None() }
        union UtiRes<T, E> { Ok(T) Err(E) }
        union UtiOther<T> { Nothing() }

        """;

    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(Prelude + source);
        return string.Join(",", results.Select(value => value?.ToString() ?? "null"));
    }

    // ── The two targets that seed inference ────────────────────────────────────

    [Fact]
    public async Task An_annotated_declaration_supplies_the_type_argument()
    {
        Assert.Equal("empty", await RunAsync(
            """
            var o: UtiOpt<int> = UtiOpt.None()
            echo (match ($o) {
                None() => "empty"
                default => "other"
            })
            """));
    }

    [Fact]
    public async Task A_declared_return_type_supplies_the_type_argument()
    {
        // The motivating case. The signature already says `int`; before this the author had to
        // say it again on the very next line.
        Assert.Equal("empty", await RunAsync(
            """
            func utiFind() -> UtiOpt<int> { return UtiOpt.None() }
            echo (match ((utiFind())) {
                None() => "empty"
                default => "other"
            })
            """));
    }

    [Theory]
    [InlineData("var r: UtiRes<int, string> = UtiRes.Err(\"bad\")")]
    [InlineData("func utiG() -> UtiRes<int, string> { return UtiRes.Err(\"bad\") }\nvar r = utiG()")]
    public async Task Both_targets_supply_every_type_parameter(string setup)
    {
        Assert.Equal("bad", await RunAsync(
            $$"""
            {{setup}}
            echo (match ($r) {
                Err(m) => $m
                default => "?"
            })
            """));
    }

    // ── What must not change ───────────────────────────────────────────────────

    [Fact]
    public async Task With_no_target_the_refusal_still_stands()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await RunAsync("echo (UtiOpt.None())"));

        Assert.Contains("cannot be inferred", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_explicit_type_argument_still_works()
    {
        Assert.Equal("empty", await RunAsync(
            """
            var o = UtiOpt.None<int>()
            echo (match ($o) {
                None() => "empty"
                default => "other"
            })
            """));
    }

    [Fact]
    public async Task Inference_from_an_argument_is_untouched()
    {
        Assert.Equal("5", await RunAsync(
            """
            var o = UtiOpt.Some(5)
            echo (match ($o) {
                Some(v) => $v
                default => 0
            })
            """));
    }

    [Fact]
    public async Task An_unannotated_declaration_does_not_borrow_the_signature()
    {
        // The leak worth guarding: inside a function returning `UtiOpt<int>`, a declaration with
        // no annotation of its own must not pick the signature up as its target. It has no
        // target, so it must refuse exactly as it would at the top level.
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await RunAsync(
            """
            func utiLeak() -> UtiOpt<int> {
                var stray = UtiOpt.None()
                return UtiOpt.None()
            }
            utiLeak()
            """));

        Assert.Contains("cannot be inferred", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_target_naming_a_different_union_seeds_nothing()
    {
        // The annotation must name *this* union to say anything about its parameters.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await RunAsync("var o: UtiOther<int> = UtiOpt.None()"));
    }
}
