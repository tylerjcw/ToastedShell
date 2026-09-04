using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// <c>is</c> and <c>as</c> answer for a refinement type — <c>TOAST-0111</c>.
/// </summary>
/// <remarks>
/// <para>
/// Measured before this: <c>var p: PosInt = 5</c> resolved the type and enforced its predicate,
/// <c>5 is PosInt</c> answered <c>false</c>, and <c>5 as PosInt</c> reported
/// <c>Unknown type 'PosInt'</c>. Three surfaces disagreed about whether the name existed, and the
/// one that lied was the one a reader trusts most — <c>5 is-not PosInt</c> answered <c>true</c>.
/// </para>
/// <para>
/// A refinement type is the thing a type test is most obviously for, so this was the larger half
/// of <c>TOAST-0105</c>'s "`is` and type annotations agree" box.
/// </para>
/// </remarks>
public sealed class RefinementTypeTestTests
{
    private const string Types = """
        type PosInt = int where _ > 0
        type BigPos = PosInt where _ > 100
        type Name = string where _.Length > 2
        type Repaired = int where _ > 0 coerce (_ == 0 ? 1 : Math.abs(_))
        """;

    private static async Task<string> RunAsync(string body)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(Types + "\n" + body);
        return results.Count == 0 ? string.Empty : results[^1]?.ToString() ?? "null";
    }

    [Theory]
    [InlineData("5 is PosInt", "True")]
    [InlineData("-1 is PosInt", "False")]
    [InlineData("0 is PosInt", "False")]
    [InlineData("5 is-not PosInt", "False")]
    [InlineData("-1 is-not PosInt", "True")]
    [InlineData("\"abcd\" is Name", "True")]
    [InlineData("\"ab\" is Name", "False")]
    public async Task A_refinement_answers_its_predicate(string expression, string expected)
    {
        Assert.Equal(expected, await RunAsync($"echo ({expression})"));
    }

    [Fact]
    public async Task A_test_does_not_convert()
    {
        // The difference between this and the annotation path. `var p: PosInt = "5"` may coerce;
        // `"5" is PosInt` is false for the same reason `"5" is int` is. A test reports what a
        // value is, never what it could become.
        Assert.Equal("False", await RunAsync("""echo ("5" is PosInt)"""));
        Assert.Equal("False", await RunAsync("""echo ("5" is int)"""));
    }

    [Theory]
    [InlineData("200 is BigPos", "True")]
    [InlineData("50 is BigPos", "False")]   // fails the outer link
    [InlineData("-5 is BigPos", "False")]   // fails the inner link
    public async Task A_refinement_over_a_refinement_checks_every_link(string expression, string expected)
    {
        Assert.Equal(expected, await RunAsync($"echo ({expression})"));
    }

    [Theory]
    [InlineData("5 is int", "True")]
    [InlineData("5 is string", "False")]
    [InlineData("\"x\" is string", "True")]
    public async Task Ordinary_type_tests_are_unchanged(string expression, string expected)
    {
        Assert.Equal(expected, await RunAsync($"echo ({expression})"));
    }

    [Fact]
    public async Task As_converts_through_the_refinement()
    {
        Assert.Equal("5", await RunAsync("echo (5 as PosInt)"));
    }

    [Fact]
    public async Task As_applies_the_coercer()
    {
        // `as` is a conversion, so it runs the `coerce` clause the annotation path runs.
        Assert.Equal("1", await RunAsync("echo (0 as Repaired)"));
        Assert.Equal("7", await RunAsync("echo (-7 as Repaired)"));
    }

    [Fact]
    public async Task As_still_fails_when_the_predicate_cannot_be_satisfied()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await RunAsync("echo (-3 as PosInt)"));

        Assert.Contains("refinement", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Unknown type", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_annotation_and_a_test_now_agree()
    {
        // The box this was filed under: a name annotations accept must be a name `is` accepts.
        Assert.Equal("True", await RunAsync("""
            var p: PosInt = 5
            echo ($p is PosInt)
            """));
    }

    // ── Qualified refinement types (`TOAST-0113`) ─────────────────────────────

    private const string Qualified = """
        module M {
            module T {
                export type Base     = string where _.Length > 0
                export type Short    = Base where _.Length < 5
                export type Repaired = int {
                    where  _ > 0
                    coerce (_ == 0 ? 1 : Math.abs(_))
                }
            }
        }
        """;

    private static async Task<string> RunQualifiedAsync(string body)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(Qualified + "\n" + body);
        return results.Count == 0 ? string.Empty : results[^1]?.ToString() ?? "null";
    }

    [Theory]
    [InlineData("\"hi\" is M.T.Base", "True")]
    [InlineData("\"\" is M.T.Base", "False")]
    [InlineData("\"hi\" is-not M.T.Base", "False")]
    [InlineData("\"hi\" is M.T::Base", "True")]
    public async Task A_qualified_refinement_answers_a_type_test(string expression, string expected)
    {
        // Before this, the right operand was evaluated as module member access and the whole
        // expression failed: "Member 'Base' was not found on type 'ToshModuleObject'". A
        // refinement type lives in a module's own table rather than in `Types`, so the member
        // lookup found nothing — while a declared class qualified the same way worked.
        Assert.Equal(expected, await RunQualifiedAsync($"echo ({expression})"));
    }

    [Theory]
    [InlineData("\"hi\" is M.T.Short", "True")]
    [InlineData("\"far too long\" is M.T.Short", "False")]
    public async Task A_qualified_chain_checks_every_link(string expression, string expected)
    {
        // `Short` derives from `Base` unqualified, so this also exercises TOAST-0104's rule that
        // a base resolves in the module its alias was declared in.
        Assert.Equal(expected, await RunQualifiedAsync($"echo ({expression})"));
    }

    [Fact]
    public async Task As_through_a_qualified_name_converts_and_coerces()
    {
        Assert.Equal("hi", await RunQualifiedAsync("""echo ("hi" as M.T.Base)"""));
        Assert.Equal("1", await RunQualifiedAsync("echo (0 as M.T.Repaired)"));
        Assert.Equal("7", await RunQualifiedAsync("echo (-7 as M.T.Repaired)"));
    }

    [Fact]
    public async Task A_qualified_cast_that_cannot_be_satisfied_still_fails()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await RunQualifiedAsync("""echo ("" as M.T.Base)"""));

        Assert.Contains("refinement", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_qualified_declared_type_is_unaffected()
    {
        // The control: classes and records were already in a module's `Types` table and answered
        // correctly. Adding refinements beside them must not disturb that.
        Assert.Equal("True", await RunAsync("""
            module Shapes {
                export record Circle(r: int)
            }

            var c = new Shapes.Circle(r = 1)
            echo ($c is Shapes.Circle)
            """));
    }
}
