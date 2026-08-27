using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A module-qualified command must accept the same arguments an unqualified one
/// does (<c>TS-P2-34</c>), and a dotted import name must reach into nested modules
/// (<c>TS-P2-35</c>).
/// </summary>
/// <remarks>
/// <para>
/// Both were reported from a real library: modules nested to form a namespace-like
/// structure, imported as <c>require ToastLib.Shell from "…" as ToastShell</c>, and
/// invoked as <c>ToastShell.HasPipe { … }</c>. Twelve parse errors and one require
/// failure, from two unrelated causes.
/// </para>
/// <para>
/// The parse half: <c>LooksLikeStaticMemberAccessExpression</c> treats a dotted name
/// in command position as a CLR member access *unless* the next token starts a
/// command argument, and <c>NextTokenStartsCommandArgument</c> listed only value
/// tokens. So a qualified command took <c>5</c> and refused <c>{ … }</c>, which made
/// it look like a limitation of blocks rather than a hole in a token list. Same
/// family as <c>TS-P2-16</c>, which fixed the value case and left this behind.
/// </para>
/// </remarks>
public sealed class QualifiedCommandArgumentTests
{
    private const string Module =
        """
        module M {
            export func F(b) -> string { return "called" }
        }
        """;

    [Theory]
    // The shapes that failed: everything delimiter-opened.
    [InlineData("{ echo hi }")]
    [InlineData("[1, 2]")]
    [InlineData("{| a = 1 |}")]
    [InlineData("{: 1, 2 :}")]
    [InlineData("{% \"k\" => 1 %}")]
    // The shapes that already worked, kept so the fix cannot trade one for another.
    [InlineData("5")]
    [InlineData("\"text\"")]
    [InlineData("(1 + 2)")]
    public async Task A_qualified_command_accepts_the_argument(string argument)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync($"{Module}\nM.F {argument}");

        Assert.Equal("called", Assert.Single(results)?.ToString());
    }

    [Fact]
    public async Task A_qualified_rune_accepts_a_block_too()
    {
        // The reported case used a `rune`, which was a red herring — the failure was
        // in command-argument recognition and had nothing to do with the callee kind.
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(
            """
            module M {
                export rune R(body) { $body }
            }
            M.R { echo "ran" }
            """);

        Assert.NotEmpty(results);
    }

    [Fact]
    public async Task An_unqualified_command_still_accepts_a_block()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(
            """
            func F(b) -> string { return "called" }
            F { echo hi }
            """);

        Assert.Equal("called", Assert.Single(results)?.ToString());
    }

    [Fact]
    public void Sibling_static_member_accesses_are_unaffected()
    {
        // The predicate's comment warns that this check is confined to command
        // position so `echo Config.version Config.maxRetries` reads both as member
        // accesses. Adding delimiters must not have disturbed that, and a following
        // *bareword* is what that case turns on.
        var result = Tosh.Language.Parsing.ToshParser.Parse(
            "echo Config.version Config.maxRetries",
            "<t>");

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void A_qualified_name_followed_by_a_block_on_the_next_line_stays_an_expression()
    {
        // The delimiter must be on the same line to count as an argument, which is
        // what `HasLineBreakBetween` already enforced. Pinned because the fix widened
        // the token set that reaches that check.
        var result = Tosh.Language.Parsing.ToshParser.Parse(
            "module M { export func F() { } }\nM.F\n{ echo hi }",
            "<t>");

        // Two statements rather than one command with a block argument; the point is
        // that it parses, not that it means something particular.
        Assert.Empty(result.Diagnostics);
    }
}
