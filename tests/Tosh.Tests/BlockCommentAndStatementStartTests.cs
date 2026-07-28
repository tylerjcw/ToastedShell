using Tosh.Language.Parsing;

namespace Tosh.Tests;

/// <summary>
/// TS-P2-06 — an unterminated block comment must not silently swallow
/// the rest of the file, and statement-boundary detection must recognise
/// every token that can legally start an expression.
/// </summary>
public sealed class BlockCommentAndStatementStartTests
{
    [Fact]
    public void An_unterminated_block_comment_is_diagnosed()
    {
        // Previously this consumed to end of input in silence, so every
        // statement after it simply never ran and nothing said why.
        var exception = Assert.Throws<ToshLexer.LexerDiagnosticException>(
            () => new ToshLexer("echo before\n##{ never closes\necho after").Lex());

        Assert.Equal("tosh.parser.unterminated_block_comment", exception.Diagnostic.Code);
    }

    [Fact]
    public void A_terminated_block_comment_still_skips_only_itself()
    {
        var result = ToshParser.Parse("echo before\n##{ closed }##\necho after", "<t>");
        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    // Every kind that can begin an expression must also be accepted as
    // the start of a statement; the boundary check used to carry a
    // shorter list of its own.
    [InlineData(SyntaxTokenKind.String)]
    [InlineData(SyntaxTokenKind.Number)]
    [InlineData(SyntaxTokenKind.Boolean)]
    [InlineData(SyntaxTokenKind.Null)]
    [InlineData(SyntaxTokenKind.UnitLiteral)]
    [InlineData(SyntaxTokenKind.InterpolatedString)]
    [InlineData(SyntaxTokenKind.Bareword)]
    [InlineData(SyntaxTokenKind.OpenBrace)]
    [InlineData(SyntaxTokenKind.OpenBraceColon)]
    [InlineData(SyntaxTokenKind.OpenBracePipe)]
    [InlineData(SyntaxTokenKind.OpenBracePercent)]
    [InlineData(SyntaxTokenKind.OpenParen)]
    [InlineData(SyntaxTokenKind.OpenBracket)]
    [InlineData(SyntaxTokenKind.DollarOpenParen)]
    [InlineData(SyntaxTokenKind.LessThanOpenParen)]
    [InlineData(SyntaxTokenKind.Ampersand)]
    public void Expression_start_kinds_are_recognised(SyntaxTokenKind kind)
    {
        Assert.True(ToshParser.IsExpressionStartToken(kind));
    }

    [Theory]
    [InlineData(SyntaxTokenKind.Pipe)]
    [InlineData(SyntaxTokenKind.Semicolon)]
    [InlineData(SyntaxTokenKind.CloseParen)]
    [InlineData(SyntaxTokenKind.CloseBrace)]
    [InlineData(SyntaxTokenKind.ColonCloseBrace)]
    [InlineData(SyntaxTokenKind.PipeCloseBrace)]
    [InlineData(SyntaxTokenKind.PercentCloseBrace)]
    [InlineData(SyntaxTokenKind.EndOfFile)]
    public void Non_expression_kinds_are_rejected(SyntaxTokenKind kind)
    {
        Assert.False(ToshParser.IsExpressionStartToken(kind));
    }

    [Theory]
    [InlineData("echo one\n$\"two\"")]
    [InlineData("echo one\n$(echo two)")]
    [InlineData("echo one\n42")]
    [InlineData("func g() { return 1 }\necho one\n&g")]
    public void A_new_line_starting_with_any_expression_token_parses_cleanly(string source)
    {
        var result = ToshParser.Parse(source, "<t>");
        Assert.Empty(result.Diagnostics);
    }
}
