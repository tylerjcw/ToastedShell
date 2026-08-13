using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A command call is a condition on its own, without a second pair of
/// parentheses.
///
/// `TS-P2-112`. `if (is-dir $path)` reported
/// `tosh.parser.missing_closing_parenthesis` with the label "this condition
/// never closes", pointing at the condition rather than at the call inside it,
/// while `if ((is-dir $path))` worked. The model was coherent — a condition is
/// an expression, and a parenthesised command call is how a call *becomes* one —
/// but `if (some-command $x)` is what anyone writes first, and nothing in the
/// diagnostic suggested the real cause.
/// </summary>
public class ConditionCommandCallTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return string.Join(",", results.Select(value => value?.ToString() ?? "null"));
    }

    private const string Positive = "func positive(n: int) -> bool => ($n > 0)\n";

    /// <summary>
    /// The parity that matters: one pair of parentheses must mean exactly what
    /// two mean, on both branches. Asserting only the true branch would pass on
    /// a change that made every command call truthy.
    /// </summary>
    [Theory]
    [InlineData("if (positive 5) { \"t\" } else { \"f\" }", "t")]
    [InlineData("if (positive -5) { \"t\" } else { \"f\" }", "f")]
    [InlineData("if ((positive 5)) { \"t\" } else { \"f\" }", "t")]
    [InlineData("if ((positive -5)) { \"t\" } else { \"f\" }", "f")]
    public async Task A_command_call_is_a_condition(string body, string expected)
        => Assert.Equal(expected, await RunAsync(Positive + body));

    [Fact]
    public async Task The_other_conditional_forms_take_it_too()
    {
        Assert.Equal("unless-ok", await RunAsync(
            Positive + "unless (positive -1) { \"unless-ok\" }"));

        Assert.Equal("elseif-ok", await RunAsync(
            Positive + "if (positive -1) { \"no\" } else if (positive 1) { \"elseif-ok\" }"));

        Assert.Equal("2,1", await RunAsync(
            Positive +
            """
            var i = 2
            while (positive $i) {
                $i
                $i = $i - 1
            }
            """));
    }

    /// <summary>
    /// A call taking several arguments, which is the shape that made the old
    /// parse stop: one argument was read and the `)` was still not next.
    /// </summary>
    [Fact]
    public async Task A_call_with_several_arguments_works()
        => Assert.Equal("yes", await RunAsync(
            """
            func between(low: int, n: int, high: int) -> bool => ($n > $low and $n < $high)
            if (between 1 5 9) { "yes" } else { "no" }
            """));

    /// <summary>
    /// The controls. These never reached the retry — they consume through to the
    /// `)` on the first read — and they must keep doing so.
    /// </summary>
    [Theory]
    [InlineData("var x = 1\nif ($x) { \"t\" } else { \"f\" }", "t")]
    [InlineData("if (true) { \"t\" } else { \"f\" }", "t")]
    [InlineData("if (false) { \"t\" } else { \"f\" }", "f")]
    [InlineData("if (Math.Sign(1)) { \"t\" } else { \"f\" }", "t")]
    [InlineData("var x = 1\nif ($x == 1) { \"t\" } else { \"f\" }", "t")]
    [InlineData("var x = 1\nif (not $x) { \"t\" } else { \"f\" }", "f")]
    public async Task An_ordinary_condition_is_unaffected(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// The negative control, and the reason the retry is bounded: a condition that
    /// genuinely never closes must still be reported. The retry re-reads the group
    /// as a pipeline, and when that fails too the original diagnostic is restored
    /// rather than replaced by whatever the second attempt made of the wreckage.
    /// </summary>
    [Fact]
    public async Task An_unterminated_condition_is_still_reported()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("if (is-dir \"/tmp\" { \"t\" }"));

        Assert.Contains(exception.Diagnostics, d => d.Code == "tosh.parser.missing_closing_parenthesis");
    }

    /// <summary>
    /// The retry must not leave the failed first attempt's diagnostics behind: a
    /// program that parses has no business reporting anything.
    /// </summary>
    [Fact]
    public void A_successful_retry_leaves_no_diagnostics()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var parse = engine.Parse(
            Positive + "if (positive 5) { \"t\" }",
            "<condition-test>");

        Assert.Empty(parse.Diagnostics);
    }
}
