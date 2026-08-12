using Tosh.Language;
using Tosh.Language.Parsing;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// <c>nameof</c> on a member chain reports the last segment — <c>TS-P2-20</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>nameof($foo.Bar)</c> answered <c>"foo"</c>: the parser kept the *first* segment of the
/// chain and discarded the rest. The worst kind of wrong answer — <c>foo</c> is a name the
/// operand does mention, so nothing about the result looks like a failure, and it is a
/// <c>string</c> either way, so no later step could catch it. C# reports the last segment, and
/// the board's intent named matching it as the first option.
/// </para>
/// <para>
/// The reduction was written twice, once for <c>nameof(...)</c> and once for the command-style
/// <c>name-of</c>, which is the drift this programme keeps finding; both now share one routine
/// and the corpus pins both spellings.
/// </para>
/// </remarks>
public sealed class NameOfMemberChainTests
{
    private static async Task<object?> EvalAsync(string script)
    {
        var engine = new ToshEngine(new ToshRuntime());
        return (await engine.ExecuteToListAsync(script)).LastOrDefault();
    }

    [Theory]
    [InlineData("var foo = {| Bar = 1 |}\nnameof($foo.Bar)", "Bar")]
    [InlineData("var foo = {| Bar = {| Baz = 1 |} |}\nnameof($foo.Bar.Baz)", "Baz")]
    [InlineData("class K { static prop S: int = 1 }\nnameof(K.S)", "S")]
    [InlineData("var foo = {| Bar = 1 |}\nname-of $foo.Bar", "Bar")]
    public async Task A_member_chain_reports_its_last_segment(string script, string expected)
    {
        Assert.Equal(expected, (await EvalAsync(script))?.ToString());
    }

    [Theory]
    [InlineData("var foo = 1\nnameof($foo)", "foo")]
    [InlineData("class K { }\nnameof(K)", "K")]
    [InlineData("nameof(echo)", "echo")]
    [InlineData("var foo = 1\nname-of $foo", "foo")]
    public async Task A_bare_name_is_unchanged(string script, string expected)
    {
        Assert.Equal(expected, (await EvalAsync(script))?.ToString());
    }

    [Fact]
    public async Task The_requires_dollar_check_still_fires_for_a_bare_variable_name()
    {
        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            async () => await EvalAsync("var foo = 1\nnameof(foo)"));

        Assert.Contains(exception.Diagnostics, d => d.Code == "tosh.runtime.nameof_requires_dollar");
    }

    [Fact]
    public async Task A_member_chain_is_exempt_from_the_requires_dollar_check()
    {
        // The last segment is a *member* name. A variable that happens to share it says nothing
        // about what was written, and demanding `nameof($S)` would name something the operand
        // never mentioned. Taking the last segment is what makes this reachable at all, so the
        // exemption ships with the fix rather than after it.
        var value = await EvalAsync("var S = 9\nclass K { static prop S: int = 1 }\nnameof(K.S)");

        Assert.Equal("S", value?.ToString());
    }

    [Fact]
    public void An_operand_with_no_name_is_reported_rather_than_reduced_to_its_root()
    {
        // A trailing dot leaves nothing to report. Falling back to `foo` would be the same silent
        // wrong answer in a different spelling.
        var result = ToshParser.Parse("nameof($foo.)", "<probe>");

        Assert.Contains(result.Diagnostics, d => d.Code == "tosh.parser.nameof_expects_a_name");
    }
}
