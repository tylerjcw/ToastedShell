using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A variable reference ends before a range operator — <c>TOAST-0121</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>$a..$b</c> scanned as one bareword, because a bareword beginning with <c>$</c>
/// swallows dots so that <c>$x.Member</c> stays one token. The parser then read the two
/// dots as an accessor and reported <c>Member '$b' was not found on type 'Int32'</c> — a
/// message about member access, for a line that contains none.
/// </para>
/// <para>
/// Only the *left* operand was ever affected: <c>1..$b</c> worked, because a number cannot
/// continue into a bareword and so the range token was emitted normally. That is what made
/// the failure look arbitrary, and it means the spelling that failed —
/// <c>for i in $first..$last</c> — is the one most likely to be written.
/// </para>
/// <para>
/// The lexer already broke a *numeric* prefix before <c>..</c> for exactly this reason, and
/// already broke a variable reference before <c>?.</c>; this is the same break, narrowed
/// the same way, so a path keeps its dots.
/// </para>
/// </remarks>
public sealed class GluedRangeOperandTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? string.Empty : results[^1]?.ToString() ?? "null";
    }

    [Theory]
    [InlineData("var a = 1\nvar b = 3\n($a..$b | count)", "3")]
    [InlineData("var a = 1\n($a..3 | count)", "3")]
    [InlineData("var b = 3\n(1..$b | count)", "3")]                       // already worked
    [InlineData("var a = 1\nvar b = 3\n($a..($b + 1) | count)", "4")]
    public async Task A_range_may_start_at_a_variable(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    [Fact]
    public async Task A_range_may_start_at_a_member()
    {
        var source =
            "class R() { prop Low = 1\n"
            + "            prop High = 4 }\n"
            + "var r = new R()\n"
            + "($r.Low..$r.High | count)";

        Assert.Equal("4", await RunAsync(source));
    }

    [Fact]
    public async Task A_glued_range_drives_a_for_loop()
    {
        // The shape it was found in: a loop over a computed span, where every
        // neighbouring spelling worked and this one did not.
        var source =
            "var first = 2\n"
            + "var last = 5\n"
            + "var total = 0\n"
            + "for i in $first..$last { $total = ($total + $i) }\n"
            + "$total";

        Assert.Equal("14", await RunAsync(source));
    }

    [Fact]
    public async Task A_doubled_dot_before_a_name_is_still_questioned()
    {
        // `$obj..Name` is worth asking about — a finger slipped on the dot — and stays a
        // diagnostic. `$a..$b` and `$a..3` are not: there is no member named `$b` or `3`,
        // so the reading the message proposes does not exist.
        var error = await Assert.ThrowsAnyAsync<ToshDiagnosticException>(
            () => RunAsync("class R() { prop Name = 1 }\nvar obj = new R()\n$obj..Name"));

        Assert.Contains(error.Diagnostics, d => d.Code == "tosh.parser.accidental_double_dot");
    }

    [Theory]
    // A path keeps its dots: the break needs the text so far to be a plain variable
    // reference, so anything with a separator in it is left as one word.
    [InlineData("var HOME = \"/tmp\"\necho $HOME/..", "$HOME/..")]
    [InlineData("echo ../x", "../x")]
    [InlineData("var HOME = \"/tmp\"\necho $HOME/...", "$HOME/...")]
    public async Task A_path_is_not_a_range(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    [Theory]
    // Everything else about a `$` word is unchanged.
    [InlineData("class R() { prop Y = 9 }\nvar x = new R()\n$x.Y", "9")]
    [InlineData("var x = null\nvar y = ($x?.Y)\n($y == null)", "True")]
    [InlineData("var a = 1\nvar b = 3\n($a .. $b | count)", "3")]
    public async Task Member_access_and_safe_navigation_are_unchanged(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));
}
