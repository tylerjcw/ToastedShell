using Tosh.Language.Parsing;

namespace Tosh.Tests;

/// <summary>
/// Differential tests for <see cref="LiteParser.LiteStageDivision"/>-producing
/// walk, groundwork for replacing <c>ToshParser.HasTopLevelPipeBeforeCloseParen</c>
/// (<c>TS-P2-24</c>).
/// </summary>
/// <remarks>
/// The helper being replaced re-scans the token stream from the parser's current
/// position with its own bracket-depth counter. These tests establish that the
/// structural pass reaches the same answer before the call sites are switched
/// over, which is the same order the element-boundary work used: agree first,
/// retire the heuristic second.
/// </remarks>
public sealed class LiteStageDivisionTests
{
    private static IReadOnlyList<LiteStageDivision> Divisions(string source)
    {
        var tokens = new ToshLexer(source).Lex();
        LiteParser.CandidateBoundaries(tokens, source, out var divisions);
        return divisions;
    }

    private static IReadOnlyList<SyntaxToken> Lex(string source) =>
        new ToshLexer(source).Lex();

    /// <summary>Index of the nth <c>(</c> token, which is a frame opener.</summary>
    private static int OpenParenIndex(string source, int occurrence = 0)
    {
        var tokens = Lex(source);
        var seen = 0;

        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Kind == SyntaxTokenKind.OpenParen && seen++ == occurrence)
            {
                return index;
            }
        }

        throw new InvalidOperationException($"no '(' #{occurrence} in: {source}");
    }

    [Theory]
    [InlineData("ls | count", 1)]
    [InlineData("ls", 0)]
    [InlineData("ls | sort | first", 2)]
    [InlineData("echo (1 | count)", 1)]
    [InlineData("echo (1 | count) | first", 2)]
    [InlineData("[1, 2] | where { _ > 1 }", 1)]
    public void Every_pipe_is_recorded_once(string source, int expected)
    {
        Assert.Equal(expected, Divisions(source).Count);
    }

    [Fact]
    public void A_pipe_is_owned_by_its_innermost_frame()
    {
        // The whole point: a pipe inside parentheses belongs to those
        // parentheses, not to the enclosing statement.
        const string source = "if (ls | count) { echo yes }";
        var parenIndex = OpenParenIndex(source);

        var division = Assert.Single(Divisions(source));
        Assert.Equal(parenIndex, division.OwnerOpenTokenIndex);
        Assert.False(division.IsPipeForward);
    }

    [Fact]
    public void A_top_level_pipe_has_no_owner()
    {
        var division = Assert.Single(Divisions("ls | count"));
        Assert.Null(division.OwnerOpenTokenIndex);
    }

    [Fact]
    public void Nested_frames_own_their_own_pipes()
    {
        // Outer parens own the first pipe; the inner ones own the second.
        const string source = "echo (a | (b | c))";
        var outer = OpenParenIndex(source, 0);
        var inner = OpenParenIndex(source, 1);

        var divisions = Divisions(source);

        Assert.Equal(2, divisions.Count);
        Assert.Equal(outer, divisions[0].OwnerOpenTokenIndex);
        Assert.Equal(inner, divisions[1].OwnerOpenTokenIndex);
    }

    [Fact]
    public void A_pipe_inside_a_block_is_not_owned_by_an_enclosing_paren()
    {
        // `where { ... }` opens a brace frame inside the parens, so the pipe
        // belongs to the block. Attributing it to the parens would make the
        // condition look like a pipeline when it is not.
        const string source = "if ([1] | where { _ | count }) { echo yes }";
        var divisions = Divisions(source);

        Assert.Equal(2, divisions.Count);
        Assert.Equal(OpenParenIndex(source), divisions[0].OwnerOpenTokenIndex);
        Assert.NotEqual(OpenParenIndex(source), divisions[1].OwnerOpenTokenIndex);
    }

    [Fact]
    public void Pipe_forward_is_recorded_as_one_division()
    {
        var division = Assert.Single(Divisions("ls |> count"));

        Assert.True(division.IsPipeForward);
        Assert.Null(division.OwnerOpenTokenIndex);
    }

    [Theory]
    // The question the helper actually answers: does the group opened by this
    // paren contain a pipe it owns? These are the shapes both call sites hinge
    // on, and they are covered behaviourally by PipelineInParenthesesTests.
    [InlineData("if (ls | count) { echo yes }", true)]
    [InlineData("if (2 + 2 > 3) { echo yes }", false)]
    [InlineData("if ([1] | where { _ | count }) { echo yes }", true)]
    [InlineData("[1, 2] | where ($_ > 1)", false)]
    public void Owner_query_matches_what_the_call_sites_ask(string source, bool expected)
    {
        var parenIndex = OpenParenIndex(source);
        var owned = Divisions(source).Any(d => d.OwnerOpenTokenIndex == parenIndex);

        Assert.Equal(expected, owned);
    }

    [Fact]
    public void Paired_literal_delimiters_own_their_pipes_too()
    {
        // A record value may contain a pipeline; that pipe belongs to the
        // literal, not to any enclosing group.
        const string source = "echo {| out = ls | count |}";
        var tokens = Lex(source);
        var literalIndex = tokens
            .Select((token, index) => (token, index))
            .First(pair => pair.token.Kind == SyntaxTokenKind.OpenBracePipe)
            .index;

        var division = Assert.Single(Divisions(source));
        Assert.Equal(literalIndex, division.OwnerOpenTokenIndex);
    }
}
