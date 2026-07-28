using Tosh.Language;
using Tosh.Language.Parsing;
using Tosh.Runtime;

namespace Tosh.Tests;

public sealed class IntrinsicLiteralAndRangeTests
{
    [Theory]
    [InlineData("2026-03-27")]
    [InlineData("2026-03-27T14:30")]
    [InlineData("2026-03-27T14:30:45")]
    [InlineData("2026-03-27T14:30:45.1234567")]
    [InlineData("2026-03-27T14:30:45Z")]
    [InlineData("2026-03-27T14:30:45-04:00")]
    [InlineData("2026-03-27T14:30:45.1234567-04:00")]
    public void Exact_iso_temporal_forms_are_intrinsic_literals(string text)
    {
        Assert.True(TemporalParser.TryParseDateTimeOffsetLiteral(text, out _));
        Assert.True(IntrinsicLiteralParser.TryParseExpressionLiteral(text, out var value));
        Assert.IsType<DateTimeOffset>(value);
    }

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("1.5..3")]
    [InlineData("127.0.0.01")]
    [InlineData("03/27/2026")]
    [InlineData("March 27, 2026")]
    public void Ambiguous_noncanonical_spellings_are_not_intrinsic_literals(string text)
    {
        Assert.False(TemporalParser.TryParseDateTimeOffsetLiteral(text, out _));
        Assert.False(IntrinsicLiteralParser.TryParseExpressionLiteral(text, out _));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("192.168.1.200")]
    [InlineData("::1")]
    [InlineData("2001:db8::1")]
    [InlineData("::ffff:192.0.2.1")]
    public void Canonical_ip_forms_remain_intrinsic_literals(string text)
    {
        Assert.True(IntrinsicLiteralParser.TryParseExpressionLiteral(text, out var value));
        Assert.IsType<System.Net.IPAddress>(value);
    }

    [Fact]
    public async Task Dotted_numeric_typo_is_not_silently_coerced_in_expression_position()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("var value = 1.2.3"));

        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal("tosh.runtime.unknown_command", diagnostic.Code);
        Assert.Contains("1.2.3", diagnostic.Title, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-1..5", -1d, 5d)]
    [InlineData("1.5..3", 1.5d, 3d)]
    [InlineData("0x1..0x3", 1d, 3d)]
    public void Signed_float_and_radix_range_heads_lex_as_ranges(
        string source,
        double expectedStart,
        double expectedEnd)
    {
        var tokens = new ToshLexer(source).Lex();

        Assert.Collection(
            tokens,
            start =>
            {
                Assert.Equal(SyntaxTokenKind.Number, start.Kind);
                Assert.Equal(expectedStart, Convert.ToDouble(start.Value));
            },
            separator => Assert.Equal(SyntaxTokenKind.DotDot, separator.Kind),
            end =>
            {
                Assert.Equal(SyntaxTokenKind.Number, end.Kind);
                Assert.Equal(expectedEnd, Convert.ToDouble(end.Value));
            },
            eof => Assert.Equal(SyntaxTokenKind.EndOfFile, eof.Kind));
    }

    [Fact]
    public void Fractional_ranges_receive_a_targeted_parser_diagnostic()
    {
        var result = ToshParser.Parse("1.5..3");

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("tosh.parser.range_requires_integer", diagnostic.Code);
        Assert.Contains("32-bit integers", diagnostic.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Negative_integer_ranges_evaluate_normally()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync("-1..2");

        Assert.Equal(new object?[] { -1, 0, 1, 2 }, results);
    }
}
