using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Where a statement ends — `TS-P2-117`.
///
/// `var r = true` followed by `not $r` parsed as **one** expression across the break, so
/// the binding never happened and the error named `r` as undeclared on the very line that
/// declared it. `and`, `bnot` and a leading `+` all did the same, while a leading *value*
/// (`6 + 3`) and the same two statements separated by `;` were fine — a boundary strange
/// enough that the item asked for the rule to be decided before anything was changed.
///
/// It needed no new rule. The language already says **the line that ends signals
/// continuation**: a trailing operator continues, and so does an unclosed bracket. A
/// leading operator was continuing as well, which is the inconsistency, and these pin both
/// halves so it cannot come back as either.
/// </summary>
public sealed class LineContinuationTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? string.Empty : results[^1]?.ToString() ?? "null";
    }

    /// <summary>
    /// A line beginning with a word operator is its own statement, so the declaration above
    /// it happens.
    /// </summary>
    [Theory]
    [InlineData("var r = true\nnot $r", "False")]
    [InlineData("var m = 12\nnot false", "True")]
    [InlineData("var r = 5\n+ 5", "5")]
    public async Task A_leading_operator_begins_a_new_statement(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// The binding survives, which is the part the defect destroyed silently.
    /// </summary>
    [Fact]
    public async Task The_declaration_above_a_leading_operator_still_binds()
        => Assert.Equal("True", await RunAsync(
            """
            var r = true
            not false
            $r
            """));

    /// <summary>
    /// A trailing operator still continues — the convention the language actually has, and
    /// the one a fix at the wrong level would have broken.
    /// </summary>
    [Fact]
    public async Task A_trailing_operator_still_continues_the_line()
        => Assert.Equal("3", await RunAsync("var r = 1 +\n2\n$r"));

    /// <summary>
    /// An unclosed bracket still continues, including with the operator leading the next
    /// line — ordinary style, and the reason the guard counts bracket depth rather than
    /// stopping at every newline.
    /// </summary>
    [Theory]
    [InlineData("var r = (1\n + 2)\n$r", "3")]
    [InlineData("var r = (true\n and false)\n$r", "False")]
    [InlineData("var r = [1,\n 2]\n($r | count)", "2")]
    public async Task An_unclosed_bracket_still_continues(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// Braces are deliberately not counted: a block holds statements, so a line inside one
    /// begins a statement exactly as a line outside it does.
    /// </summary>
    [Fact]
    public async Task A_leading_operator_inside_a_block_also_begins_a_statement()
        => Assert.Equal("False", await RunAsync(
            """
            func f() {
                var r = true
                not $r
            }
            f()
            """));

    /// <summary>
    /// The forms that already worked, kept as controls: the `;` spelling, and a leading
    /// value rather than a leading operator.
    /// </summary>
    [Theory]
    [InlineData("var r = true; not $r", "False")]
    [InlineData("var r = 5\n6 + 3", "9")]
    [InlineData("var a = 1\nvar b = 2\n($a + $b)", "3")]
    public async Task The_forms_that_already_worked_still_do(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// An operator still works as an operator on one line — the guard is about line
    /// *position*, not about the token.
    /// </summary>
    [Fact]
    public async Task An_operator_still_operates_within_a_line()
        => Assert.Equal("False", await RunAsync("(true and false)"));
}
