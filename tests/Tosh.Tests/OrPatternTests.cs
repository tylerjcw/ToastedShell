using Tosh.Language;

namespace Tosh.Tests;

/// <summary>
/// Or-patterns and <c>as</c>-binding in a match arm — <c>TOAST-0053</c>.
/// </summary>
/// <remarks>
/// <para>
/// An or-pattern is always parenthesised, because <c>|</c> is the pipeline separator everywhere
/// else: the parens are what say this one is an alternative. An arm that was already a
/// parenthesised expression keeps its meaning, because anything that does not fit the form
/// backtracks to where it started.
/// </para>
/// <para>
/// The separator is <c>|</c> rather than the language's own <c>or</c> / <c>||</c>, which was
/// weighed and settled deliberately. <c>|</c> cannot collide: it is a parse error in pattern
/// position today, so no arm changes meaning. <c>or</c> and <c>||</c> would reinterpret an arm
/// like <c>($a or $b)</c> — which runs today, evaluating a boolean and comparing it — as two
/// alternatives, giving a different answer whenever the subject is false and either side true.
/// </para>
/// <para>
/// Only a destructuring pattern may carry <c>as</c>. After a literal it would be the cast
/// operator — <c>"a" as string</c> already means something — and the destructuring forms are
/// where wanting the parts and the whole at once actually comes up.
/// </para>
/// </remarks>
public sealed class OrPatternTests
{
    private const string Shapes = """
        record Point(x: int, y: int)
        union Expr {
            Lit(v: int)
            Add(l: int, r: int)
        }
        """;

    private static async Task<IReadOnlyList<object?>> RunAsync(string body)
    {
        var engine = ShellEngine.CreateFullShell();
        return await engine.ExecuteToListAsync(Shapes + "\n" + body);
    }

    [Fact]
    public async Task An_or_pattern_takes_either_alternative()
    {
        var results = await RunAsync("""
            echo (match (Expr.Lit(0)) {
                (Lit(0) | Lit(1)) => "yes"
                default => "no"
            })
            echo (match (Expr.Lit(1)) {
                (Lit(0) | Lit(1)) => "yes"
                default => "no"
            })
            echo (match (Expr.Lit(2)) {
                (Lit(0) | Lit(1)) => "yes"
                default => "no"
            })
            """);

        Assert.Equal("yes", results[^3]?.ToString());
        Assert.Equal("yes", results[^2]?.ToString());
        Assert.Equal("no", results[^1]?.ToString());
    }

    [Fact]
    public async Task An_or_pattern_may_hold_more_than_two_alternatives()
    {
        var results = await RunAsync("""
            echo (match (Expr.Lit(2)) {
                (Lit(0) | Lit(1) | Lit(2)) => "yes"
                default => "no"
            })
            """);

        Assert.Equal("yes", results[^1]?.ToString());
    }

    /// <summary>
    /// Alternatives may be bare values, not only destructuring patterns.
    /// </summary>
    [Fact]
    public async Task An_alternative_may_be_a_literal()
    {
        var results = await RunAsync("""
            echo (match (2) {
                (1 | 2) => "small"
                default => "big"
            })
            """);

        Assert.Equal("small", results[^1]?.ToString());
    }

    /// <summary>
    /// A parenthesised expression arm keeps its old meaning — the form backtracks cleanly when
    /// there is no <c>|</c> to make it an alternative.
    /// </summary>
    [Fact]
    public async Task A_parenthesised_expression_arm_is_unchanged()
    {
        var results = await RunAsync("""
            echo (match (3) {
                (1 + 2) => "three"
                default => "no"
            })
            """);

        Assert.Equal("three", results[^1]?.ToString());
    }

    [Fact]
    public async Task Every_alternative_binds_the_same_names()
    {
        var results = await RunAsync("""
            echo (match (Expr.Add(5, 2)) {
                (Lit(a) | Add(a, _)) => $a
                default => -1
            })
            echo (match (Expr.Lit(9)) {
                (Lit(a) | Add(a, _)) => $a
                default => -1
            })
            """);

        Assert.Equal("5", results[^2]?.ToString());
        Assert.Equal("9", results[^1]?.ToString());
    }

    /// <summary>
    /// Which alternative matched decides what the names hold, so it is found again at binding
    /// time rather than guessed from the shape.
    /// </summary>
    /// <remarks>
    /// Both alternatives here have the same shape and differ only in which position holds the
    /// literal, so binding from the first one that structurally fits would bind zero half the
    /// time. Asking the matcher is the only answer that is right in both directions.
    /// </remarks>
    [Fact]
    public async Task Alternatives_of_the_same_shape_bind_the_one_that_matched()
    {
        var results = await RunAsync("""
            echo (match (new Point(0, 7)) {
                (Point(a, 0) | Point(0, a)) => $a
                default => -1
            })
            echo (match (new Point(7, 0)) {
                (Point(a, 0) | Point(0, a)) => $a
                default => -1
            })
            """);

        Assert.Equal("7", results[^2]?.ToString());
        Assert.Equal("7", results[^1]?.ToString());
    }

    /// <summary>
    /// Alternatives that bind different names are reported where they are written.
    /// </summary>
    /// <remarks>
    /// An unset variable is not an error anywhere else in the language, so an arm reading a
    /// name only one side binds would simply do something wrong whenever the other side
    /// matched. This is the only place it can be caught.
    /// </remarks>
    [Fact]
    public async Task Alternatives_binding_different_names_are_reported()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await RunAsync("""
            echo (match (Expr.Lit(9)) {
                (Lit(v) | Add(l, r)) => $v
                default => -1
            })
            """));

        Assert.Contains("same names", error.Message);
    }

    [Fact]
    public async Task As_binds_the_whole_alongside_the_parts()
    {
        var results = await RunAsync("""
            echo (match (Expr.Add(1, 5)) {
                Add(l, r) as whole => $whole.r
                default => -1
            })
            """);

        Assert.Equal("5", results[^1]?.ToString());
    }

    /// <summary>
    /// The item's own example: keep the whole while choosing between shapes.
    /// </summary>
    [Fact]
    public async Task As_composes_with_an_or_pattern()
    {
        var results = await RunAsync("""
            echo (match (Expr.Lit(1)) {
                (Lit(0) | Lit(1)) as lit => $lit.v
                default => -1
            })
            """);

        Assert.Equal("1", results[^1]?.ToString());
    }

    /// <summary>
    /// Or-patterns nest wherever a sub-pattern goes.
    /// </summary>
    [Fact]
    public async Task An_or_pattern_nests_inside_other_patterns()
    {
        var results = await RunAsync("""
            echo (match (Expr.Add(1, 5)) {
                Add((1 | 2), r) => $r
                default => -1
            })
            echo (match (Expr.Add(3, 5)) {
                Add((1 | 2), r) => $r
                default => -1
            })
            echo (match ([2, 9]) {
                [(1 | 2), b] => $b
                default => -1
            })
            """);

        Assert.Equal("5", results[^3]?.ToString());
        Assert.Equal("-1", results[^2]?.ToString());
        Assert.Equal("9", results[^1]?.ToString());
    }
}
