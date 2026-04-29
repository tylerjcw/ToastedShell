using System.Threading;
using System.Threading.Tasks;
using Tosh.Language.Parsing;

namespace Tosh.Tests;

// Regression guards for parser infinite-loop / memory-exhaustion bugs.
// Each scenario must return a parse result within a tight budget instead of
// spinning forever and leaking memory (as a stale LSP server would).
public sealed class ParserProgressGuardTests
{
    private static void AssertParsesWithinBudget(string source, int milliseconds = 2000)
    {
        var task = Task.Run(() => ToshParser.Parse(source));
        var finished = task.Wait(TimeSpan.FromMilliseconds(milliseconds));
        Assert.True(finished, $"Parser did not return within {milliseconds}ms for source: {source}");
    }

    [Fact]
    public void Where_block_with_command_subexpression_does_not_hang()
    {
        AssertParsesWithinBudget("[1, 2] | where { (is-file $_) }");
    }

    [Fact]
    public void Where_block_with_command_dot_access_does_not_hang()
    {
        AssertParsesWithinBudget("[\"a\", \"b\"] | where { (is-file $_).IsFile } | first");
    }

    [Fact]
    public void Bare_where_block_with_command_subexpression_does_not_hang()
    {
        AssertParsesWithinBudget("where { (echo $_) }");
    }

    // Broad coverage: each bounded construct that drives a `while (Current != <terminator>)`
    // loop must guarantee forward progress when its inner parser rejects a token.
    // Every case must return within the per-case budget — if one hangs, the test fails
    // and the failing source is recorded in the assertion message.
    [Theory]
    [InlineData("for x in [1] {", "}")]
    [InlineData("while (true) {", "}")]
    [InlineData("if (true) {", "} else { }")]
    [InlineData("try {", "} catch ($e) { }")]
    [InlineData("func f() {", "}")]
    [InlineData("class C {", "}")]
    [InlineData("[1] | where {", "}")]
    [InlineData("[", "]")]
    [InlineData("(", ")")]
    [InlineData("{", "}")]
    [InlineData("[1 <| for x in [1] where", "]")]
    [InlineData("switch (1) { case 1 {", "} }")]
    [InlineData("record R(", ")")]
    [InlineData("func f(", ") { }")]
    [InlineData("module M {", "}")]
    [InlineData("enum E {", "}")]
    [InlineData("interface I {", "}")]
    public void Bounded_constructs_terminate_on_pathological_inputs(string pre, string post)
    {
        string[] nasties =
        [
            "(is-file $_)", "(echo $_)", "(foo bar)", "[|]", "{ (is-file $_) }",
            "=>", "--x", "(=)", ": string",
        ];
        foreach (var nasty in nasties)
        {
            AssertParsesWithinBudget($"{pre} {nasty} {post}", milliseconds: 500);
        }
    }
}
