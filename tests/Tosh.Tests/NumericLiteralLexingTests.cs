using Tosh.Language.Parsing;

namespace Tosh.Tests;

/// <summary>
/// TS-P2-05 — digit separators must sit between digits, a leading
/// underscore names an identifier rather than a number, and a radix
/// literal too large for 64 bits reports a structured diagnostic instead
/// of leaking the CLR's "too large or too small for a UInt64" message.
/// </summary>
public sealed class NumericLiteralLexingTests
{
    [Theory]
    [InlineData("1_000")]
    [InlineData("1_000_000")]
    [InlineData("-1_000")]
    [InlineData("0xFF")]
    [InlineData("0b1010")]
    [InlineData("0o777")]
    [InlineData("42")]
    public void Valid_numeric_literals_lex_as_numbers(string source)
    {
        var tokens = new ToshLexer(source).Lex();
        Assert.Equal(SyntaxTokenKind.Number, tokens[0].Kind);
    }

    [Theory]
    [InlineData("1__2")]
    [InlineData("1_")]
    [InlineData("0x_FF")]
    public void Misplaced_digit_separators_are_diagnosed(string source)
    {
        var exception = Assert.Throws<ToshLexer.LexerDiagnosticException>(
            () => new ToshLexer(source).Lex());

        Assert.Equal("tosh.parser.invalid_numeric_separator", exception.Diagnostic.Code);
    }

    [Theory]
    // A leading underscore makes it a name, so `var _1 = 99` binds a
    // variable instead of the lexer reading the literal 1.
    [InlineData("_1")]
    [InlineData("_count")]
    [InlineData("my_var")]
    [InlineData("read_file")]
    public void Underscore_names_stay_identifiers(string source)
    {
        var tokens = new ToshLexer(source).Lex();
        Assert.Equal(SyntaxTokenKind.Bareword, tokens[0].Kind);
        Assert.Equal(source, tokens[0].Text);
    }

    [Theory]
    [InlineData("0b11111111111111111111111111111111111111111111111111111111111111111")]
    [InlineData("0o777777777777777777777777777")]
    public void Oversized_radix_literals_report_a_structured_diagnostic(string source)
    {
        var exception = Assert.Throws<ToshLexer.LexerDiagnosticException>(
            () => new ToshLexer(source).Lex());

        Assert.Equal("tosh.parser.numeric_literal_overflow", exception.Diagnostic.Code);
    }
}
