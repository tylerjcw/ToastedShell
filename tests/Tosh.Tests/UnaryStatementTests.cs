using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A unary operator can open a statement, not only sit inside parentheses.
///
/// `TS-P2-116`. `not`, `bnot` and a spaced `-` at the start of a stage were read
/// as command names. `bnot 5` reported `Command 'bnot' was not found`, and
/// `var x = not true` did something worse — it bound nothing, printed nothing,
/// and exited 0.
///
/// The gap survived because every existing test and probe writes `echo (not
/// true)`: inside parentheses the argument parser is already in expression mode,
/// so the operators worked everywhere they were being looked at.
///
/// It is the unary half of the trap `TS-P2-105` describes. The binary operators
/// are found by the `HasTopLevelOperator…` scans, which look for an operator
/// *after* the leading token — and a unary operator is the leading token, so no
/// scan could ever see it. `LooksLikeExpressionStage` decides instead.
///
/// Found while adding the bitwise operators (`TS-P3-14`), which is why `bnot`
/// appears here at all; `not` and `-` had been wrong since long before.
/// </summary>
public class UnaryStatementTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return string.Join(",", results.Select(v => v?.ToString() ?? "null"));
    }

    /// <summary>A bare unary expression is the whole statement.</summary>
    [Theory]
    [InlineData("not true", "False")]
    [InlineData("bnot 5", "-6")]
    [InlineData("- 5", "-5")]
    [InlineData("+ 5", "5")]
    [InlineData("var m = 12\nbnot $m", "-13")]
    // Written with `;` rather than a line break: `var r = true` followed by a
    // *line* starting with `not` is absorbed into the previous expression as a
    // continuation, which is `TS-P2-117` and older than this fix. `var m = 12`
    // above does not absorb it, which is what makes that item's boundary odd
    // enough to be worth its own entry.
    [InlineData("var r = true; not $r", "False")]
    // An operand that is itself an expression, and a doubled operator.
    [InlineData("not (1 > 2)", "True")]
    [InlineData("bnot bnot 5", "5")]
    public async Task A_statement_can_open_with_a_unary_operator(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// The silent case, and the reason this is filed as a correctness defect
    /// rather than a parse inconvenience: the binding produced no value and no
    /// error. Asserting the statement form alone would leave this passing.
    /// </summary>
    [Theory]
    [InlineData("var x = not true\n$x", "False")]
    [InlineData("var x = bnot 5\n$x", "-6")]
    [InlineData("var m = 12\nvar x = bnot $m\n$x", "-13")]
    public async Task A_unary_expression_binds_to_a_variable(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// The parenthesized spellings that worked all along still do — the fix must
    /// add a reading, not replace one.
    /// </summary>
    [Theory]
    [InlineData("echo (not true)", "False")]
    [InlineData("echo (bnot 5)", "-6")]
    [InlineData("var x = (not true)\n$x", "False")]
    public async Task The_parenthesized_spelling_is_unchanged(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// The control that constrains the fix. `-` is an ordinary word when nothing
    /// follows it to negate — `cat -` must keep meaning `cat -`, and a flag like
    /// `-Force` lexes as one word and was never in question.
    /// </summary>
    [Theory]
    [InlineData("echo -", "-")]
    [InlineData("echo - ", "-")]
    [InlineData("echo -Force", "-Force")]
    public async Task A_dash_with_nothing_to_negate_is_still_a_word(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// A command whose name merely begins a line is still a command. Without the
    /// operand test above, "starts with a bareword that could be an operator"
    /// would have swallowed these.
    /// </summary>
    [Theory]
    [InlineData("echo hello", "hello")]
    [InlineData("[1, 2, 3] | count", "3")]
    public async Task An_ordinary_command_still_parses_as_one(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));
}
