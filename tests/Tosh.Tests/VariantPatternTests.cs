using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Match arms that bind a union variant's fields — <c>TOAST-0053</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every pattern form before this one tested the matched value as a whole and bound nothing,
/// so dispatching on a union meant a <c>switch</c> over <c>$r.Variant</c> followed by
/// unchecked member access: a typo in the string was a silent miss and a typo in the field a
/// runtime error, neither reported where it was written.
/// </para>
/// <para>
/// This is the first slice — positional binding only. Field patterns by name, list patterns
/// with a rest binding, nesting, or-patterns and <c>as</c> remain.
/// </para>
/// </remarks>
public sealed class VariantPatternTests
{
    private const string Unions = """
        union Result {
            Ok(value: int)
            Err(message: string)
        }
        union Expr {
            Lit(v: int)
            Add(l: int, r: int)
        }
        """;

    private static async Task<IReadOnlyList<object?>> RunAsync(string body)
    {
        var engine = ShellEngine.CreateFullShell();
        return await engine.ExecuteToListAsync(Unions + "\n" + body);
    }

    [Fact]
    public async Task A_variant_pattern_binds_its_field()
    {
        var results = await RunAsync("""
            var r = Result.Ok(42)
            echo (match ($r) {
                Ok(v) => $v
                default => 0
            })
            """);

        Assert.Equal("42", results[^1]?.ToString());
    }

    /// <summary>
    /// The binding is the field, not the variant tag.
    /// </summary>
    /// <remarks>
    /// The first implementation bound against <c>GetMembers()</c>, which prepends a
    /// <c>Variant</c> entry, so <c>Ok(v)</c> bound <c>v</c> to the string "Ok" and every
    /// pattern was one position out — while still matching, so nothing failed loudly.
    /// Binding reads the variant's declared field names now, and this is what says so.
    /// </remarks>
    [Fact]
    public async Task A_binding_is_the_field_and_not_the_variant_tag()
    {
        var results = await RunAsync("""
            var r = Result.Ok(42)
            echo (match ($r) {
                Ok(v) => ($v == 42)
                default => false
            })
            """);

        Assert.Equal("True", results[^1]?.ToString());
    }

    [Fact]
    public async Task Arms_select_by_variant()
    {
        var results = await RunAsync("""
            var r = Result.Err("bad")
            echo (match ($r) {
                Ok(v) => $v
                Err(m) => $m
                default => "none"
            })
            """);

        Assert.Equal("bad", results[^1]?.ToString());
    }

    [Fact]
    public async Task Several_fields_bind_positionally()
    {
        var results = await RunAsync("""
            var e = Expr.Add(3, 4)
            echo (match ($e) {
                Add(l, r) => ($l + $r)
                default => 0
            })
            """);

        Assert.Equal("7", results[^1]?.ToString());
    }

    /// <summary>An underscore holds a position without binding it.</summary>
    [Fact]
    public async Task Underscore_discards_a_position()
    {
        var results = await RunAsync("""
            var e = Expr.Add(3, 4)
            echo (match ($e) {
                Add(_, r) => $r
                default => 0
            })
            """);

        Assert.Equal("4", results[^1]?.ToString());
    }

    /// <summary>A guard reads what the pattern bound.</summary>
    /// <remarks>
    /// `Add(l, r) if ($l is Lit)` is the shape the item was filed for. A guard evaluated
    /// outside the pattern's bindings would make every such arm unwritable, so the binding
    /// scope is pushed before the guard runs and not only before the body.
    /// </remarks>
    [Fact]
    public async Task A_guard_sees_the_bindings()
    {
        var results = await RunAsync("""
            var e = Expr.Add(3, 4)
            echo (match ($e) {
                Add(l, r) if ($l > 10) => "big"
                Add(l, r) => "small"
                default => "none"
            })
            """);

        Assert.Equal("small", results[^1]?.ToString());
    }

    /// <summary>A bound name is gone once the arm is.</summary>
    [Fact]
    public async Task A_binding_does_not_escape_its_arm()
    {
        await Assert.ThrowsAnyAsync<Exception>(async () => await RunAsync("""
            var e = Expr.Add(3, 4)
            echo (match ($e) {
                Add(l, r) => $l
                default => 0
            })
            echo $l
            """));
    }

    /// <summary>Two arms may bind the same name for different things.</summary>
    /// <remarks>The control for arm scoping: without it, the test above could pass because
    /// binding never happened at all.</remarks>
    [Fact]
    public async Task Two_arms_may_reuse_a_name()
    {
        var results = await RunAsync("""
            var e = Expr.Lit(9)
            echo (match ($e) {
                Add(l, r) => $l
                Lit(l) => $l
                default => 0
            })
            """);

        Assert.Equal("9", results[^1]?.ToString());
    }

    /// <summary>A binding shadows an outer name and restores it.</summary>
    [Fact]
    public async Task An_outer_variable_survives_a_binding_of_the_same_name()
    {
        var results = await RunAsync("""
            var l = "outer"
            var e = Expr.Add(3, 4)
            echo (match ($e) {
                Add(l, r) => $l
                default => 0
            })
            echo $l
            """);

        Assert.Equal("outer", results[^1]?.ToString());
    }

    /// <summary>
    /// `Ok (v)` with a space is a command and its argument, as it always was.
    /// </summary>
    /// <remarks>
    /// The parser recognises a variant pattern only when the paren abuts the name. Without
    /// that, adding this form would have quietly changed what an existing arm meant.
    /// </remarks>
    [Fact]
    public async Task A_space_before_the_paren_is_not_a_variant_pattern()
    {
        var results = await RunAsync("""
            var r = Result.Ok(42)
            echo (match ($r) {
                "Ok" => "string arm"
                default => "none"
            })
            """);

        Assert.Equal("none", results[^1]?.ToString());
    }
}
