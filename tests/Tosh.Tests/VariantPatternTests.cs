using Tosh.Language;
using Tosh.Language.Binding;
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
/// Patterns bind positionally (<c>Add(l, r)</c>) or by name (<c>Lit { v }</c>), and nest to
/// any depth, because a sub-pattern is an <c>ArgumentSyntax</c> like any other — the same
/// type the outer pattern is made of. List patterns with a rest binding, or-patterns and
/// <c>as</c> remain.
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

    /// <summary>
    /// A recursive union, so a pattern has something to nest into.
    /// </summary>
    private const string Trees = """
        union Expr {
            Lit(v: int)
            Add(l: Expr, r: Expr)
        }
        union Opt {
            Some(value: Expr)
            None()
        }
        union Item {
            Node(kind: string, body: int)
        }
        """;

    private static async Task<IReadOnlyList<object?>> RunTreesAsync(string body)
    {
        var engine = ShellEngine.CreateFullShell();
        return await engine.ExecuteToListAsync(Trees + "\n" + body);
    }

    /// <summary>
    /// Runs with the binder strict, the way the CLI runs a script.
    /// </summary>
    /// <remarks>
    /// The default is <c>Warn</c>, under which a binder diagnostic is written to the error
    /// stream rather than thrown — so a test that expects a bind-time report has to ask for the
    /// strictness the CLI uses, or it would pass whether or not the check existed.
    /// </remarks>
    private static async Task<IReadOnlyList<object?>> RunTreesStrictAsync(string body)
    {
        var engine = ShellEngine.CreateFullShell();
        using var strict = engine.PushBinderStrictness(BinderStrictness.Strict);
        return await engine.ExecuteToListAsync(Trees + "\n" + body);
    }

    [Fact]
    public async Task A_field_pattern_binds_by_name()
    {
        var results = await RunTreesAsync("""
            echo (match (Expr.Lit(9)) {
                Lit { v } => $v
                default => -1
            })
            """);

        Assert.Equal("9", results[^1]?.ToString());
    }

    /// <summary>
    /// `field: name` binds the field under a different name.
    /// </summary>
    [Fact]
    public async Task A_field_pattern_may_rename_what_it_binds()
    {
        var results = await RunTreesAsync("""
            echo (match (Expr.Lit(9)) {
                Lit { v: got } => $got
                default => -1
            })
            """);

        Assert.Equal("9", results[^1]?.ToString());
    }

    /// <summary>
    /// Naming a field is not the same as binding it: the right of the colon is a pattern, so
    /// it may be a literal that has to match, alongside a shorthand that binds.
    /// </summary>
    [Fact]
    public async Task A_field_pattern_may_test_one_field_and_bind_another()
    {
        var results = await RunTreesAsync("""
            echo (match (Item.Node("if", 5)) {
                Node { kind: "if", body } => $body
                default => -1
            })
            echo (match (Item.Node("fn", 5)) {
                Node { kind: "if", body } => $body
                default => -1
            })
            """);

        Assert.Equal("5", results[^2]?.ToString());
        Assert.Equal("-1", results[^1]?.ToString());
    }

    /// <summary>
    /// A sub-pattern may be a variable reference, which compares — it does not bind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lexer hands a variable back as a bareword whose text carries the <c>$</c>, and the
    /// first implementation asked only for the token kind. So <c>$x</c> took the "a plain name
    /// binds" path and matched anything, silently, because rebinding an existing name is legal.
    /// Literals and parenthesised expressions compared correctly throughout, which is what hid
    /// it: only the *miss* case can catch this, so both directions are asserted here.
    /// </para>
    /// <para>
    /// This is also why <c>VariableBinder</c> walks sub-patterns —
    /// <c>SyntaxTraversalExhaustivenessTests</c> caught the missing traversal the moment the
    /// node was added, and without it the reference would be invisible to capture analysis.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_variable_sub_pattern_compares_rather_than_binds()
    {
        var results = await RunTreesAsync("""
            var expected = "if"
            echo (match (Item.Node("if", 5)) {
                Node { kind: $expected, body } => $body
                default => -1
            })
            echo (match (Item.Node("fn", 5)) {
                Node { kind: $expected, body } => $body
                default => -1
            })
            var wanted = 5
            echo (match (Expr.Lit(9)) {
                Lit($wanted) => 1
                default => -1
            })
            """);

        Assert.Equal("5", results[^3]?.ToString());
        Assert.Equal("-1", results[^2]?.ToString());
        Assert.Equal("-1", results[^1]?.ToString());
    }

    /// <summary>
    /// A variable sub-pattern reads the closure's captured value, not a fresh binding.
    /// </summary>
    [Fact]
    public async Task A_variable_sub_pattern_sees_a_captured_value()
    {
        var results = await RunTreesAsync("""
            func makeMatcher(expected) {
                return func(n) {
                    return match ($n) {
                        Node { kind: $expected, body } => $body
                        default => -1
                    }
                }
            }
            var m = makeMatcher("if")
            echo ($m(Item.Node("if", 5)))
            echo ($m(Item.Node("fn", 5)))
            """);

        Assert.Equal("5", results[^2]?.ToString());
        Assert.Equal("-1", results[^1]?.ToString());
    }

    /// <summary>
    /// Patterns nest to arbitrary depth, mixing positional and named forms.
    /// </summary>
    [Fact]
    public async Task Patterns_nest()
    {
        var results = await RunTreesAsync("""
            echo (match (Opt.Some(Expr.Add(Expr.Lit(3), Expr.Lit(4)))) {
                Some(Add(Lit(a), Lit(b))) => $a + $b
                default => -1
            })
            """);

        Assert.Equal("7", results[^1]?.ToString());
    }

    /// <summary>
    /// A nested pattern that does not match falls through rather than binding null.
    /// </summary>
    [Fact]
    public async Task A_nested_pattern_that_misses_falls_through()
    {
        var results = await RunTreesAsync("""
            echo (match (Opt.Some(Expr.Lit(3))) {
                Some(Add(l, r)) => 1
                default => -1
            })
            """);

        Assert.Equal("-1", results[^1]?.ToString());
    }

    [Fact]
    public async Task A_nested_field_pattern_binds()
    {
        var results = await RunTreesAsync("""
            echo (match (Opt.Some(Expr.Lit(3))) {
                Some(Lit { v }) => $v
                default => -1
            })
            """);

        Assert.Equal("3", results[^1]?.ToString());
    }

    /// <summary>
    /// Naming a field the variant does not have is reported, not silently missed.
    /// </summary>
    /// <remarks>
    /// The failure mode this replaces is the one the item opens with: a typo in the field of
    /// a <c>switch</c>-on-string dispatch was a runtime error somewhere else, or nothing.
    /// </remarks>
    [Fact]
    public async Task An_unknown_field_is_diagnosed()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await RunTreesAsync("""
            echo (match (Expr.Lit(9)) {
                Lit { valu } => 1
                default => -1
            })
            """));

        Assert.Contains("valu", error.Message);
        Assert.Contains("Lit", error.Message);
    }

    /// <summary>
    /// A positional pattern naming more fields than the variant declares is reported too.
    /// </summary>
    [Fact]
    public async Task A_wrong_arity_pattern_is_diagnosed()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await RunTreesAsync("""
            echo (match (Expr.Lit(9)) {
                Lit(a, b) => 1
                default => -1
            })
            """));

        Assert.Contains("Lit", error.Message);
        Assert.Contains("2", error.Message);
    }

    /// <summary>
    /// The diagnostics above fire on the variant the pattern names. A pattern for a
    /// *different* variant is an ordinary miss, not an error — otherwise no match over a
    /// union with more than one variant could ever run.
    /// </summary>
    [Fact]
    public async Task A_pattern_for_another_variant_is_a_miss_and_not_an_error()
    {
        var results = await RunTreesAsync("""
            echo (match (Expr.Add(Expr.Lit(1), Expr.Lit(2))) {
                Lit(v) => $v
                default => -1
            })
            """);

        Assert.Equal("-1", results[^1]?.ToString());
    }

    /// <summary>
    /// A bad field is caught where it is written, not when the arm is reached.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The runtime check fires only if the arm runs, so a mistake in an arm the test data never
    /// reaches stays hidden. The binder collects union and record declarations from the same
    /// source — the way it already collects function declarations — and checks the pattern
    /// against them, so the arm below is reported even though the value is an <c>Add</c>.
    /// </para>
    /// <para>
    /// Same-source only, deliberately. A type from a <c>require</c>d file is not collected and
    /// a pattern naming one is left alone: a missed check costs a runtime diagnostic that still
    /// names the field, while a false one costs a program that will not run.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_unknown_field_is_reported_even_in_an_arm_that_never_runs()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await RunTreesStrictAsync("""
            echo (match (Expr.Add(Expr.Lit(1), Expr.Lit(2))) {
                Lit { valu } => 1
                default => -1
            })
            """));

        Assert.Contains("valu", error.Message);
    }

    [Fact]
    public async Task A_wrong_arity_pattern_is_reported_even_in_an_arm_that_never_runs()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await RunTreesStrictAsync("""
            echo (match (Expr.Add(Expr.Lit(1), Expr.Lit(2))) {
                Lit(a, b) => 1
                default => -1
            })
            """));

        Assert.Contains("Lit", error.Message);
    }

    /// <summary>
    /// A nested pattern is checked too, not only the outermost one.
    /// </summary>
    [Fact]
    public async Task A_nested_unknown_field_is_reported()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await RunTreesStrictAsync("""
            echo (match (Expr.Lit(1)) {
                Add(Lit { nope }, r) => 1
                default => -1
            })
            """));

        Assert.Contains("nope", error.Message);
    }

    /// <summary>
    /// A pattern naming a type this source cannot see is left alone, so a `require`d type still
    /// works — the runtime check remains the backstop.
    /// </summary>
    [Fact]
    public async Task A_pattern_for_an_undeclared_type_is_not_reported()
    {
        var results = await RunTreesAsync("""
            echo (match (Expr.Lit(1)) {
                Imported { whatever } => 1
                default => -1
            })
            """);

        Assert.Equal("-1", results[^1]?.ToString());
    }
}
